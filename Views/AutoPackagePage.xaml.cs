// =====================================================================
//  AutoPackagePage.xaml.cs
//  SwiftPack Studio — Auto Package feature
//
//  Drop-in path:  SwiftPack_v8/Views/AutoPackagePage.xaml.cs
//  Config file :  C:\Program Files\Smekal\SwiftPack Studio\Templates\AutoPackage.json
//
//  Flow (MSI tab → Create):
//    1. Validate source (mandatory)
//    2. Build folder structure  Path1 = PackageRootPath\PackageName
//                               Path2 = Path1\Package
//    3. Copy template → Path2
//    4. Copy source    → Path2\Files
//    5. (opt) Create MST in Path2\Files
//    6. (opt) Remove desktop shortcut from MST
//    7. Apply MST properties from AutoPackage.json
//    8. Patch Invoke-AppDeployToolkit.ps1 install / uninstall sections
//    9. (opt) Create intunewin → Path1\Intune\Invoke-AppDeployToolkit.intunewin
//   10. (opt) Publish to Intune via Graph API
//   11. (opt) Assign to AAD group
//   12. Write %TEMP% log
//   13. Show Open Log / Open Folder buttons
// =====================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using IOPath = System.IO.Path;
using Microsoft.Win32;

namespace SwiftPack.Views
{
    // ══════════════════════════════════════════════════════════════════════
    //  JSON MODELS
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deserializes both JSON booleans (true/false) and string values ("Yes"/"No")
    /// so AutoPackage.json can use either format for flag fields.
    /// </summary>
    internal class YesNoBoolConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.True)  return true;
            if (reader.TokenType == JsonTokenType.False) return false;
            if (reader.TokenType == JsonTokenType.String)
            {
                string? s = reader.GetString();
                if (string.Equals(s, "yes",  StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                return false;  // "No", "false", empty, or anything else → false
            }
            return false;
        }
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
            => writer.WriteBooleanValue(value);
    }

    // Tolerates the number arriving as a JSON string ("587" or ""), which is how the
    // Settings window saves every field. Without this, a blank SmtpPort ("") throws in
    // JsonSerializer.Deserialize and LoadConfig resets the ENTIRE config to defaults,
    // which is how a saved MailSenderAddress read back as empty and the publish header
    // showed "ClientId : [EMPTY]". "" or garbage reads as 0; callers treat <=0 as
    // "use the default".
    internal class FlexibleIntConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int n)) return n;
            if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out int s)) return s;
            return 0;
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value);
    }

    internal class AutoPackageConfig
    {
        [JsonPropertyName("PackageOutputFolder")]  // new key
        public string PackageOutputFolder   { get; set; } = "";

        [JsonPropertyName("PackageRootPath")]      // legacy key
        public string PackageRootPath       { get; set; } = @"C:\Temp";

        // Resolved: prefer PackageOutputFolder, fall back to PackageRootPath
        [JsonIgnore]
        public string ResolvedPackageOutputFolder =>
            !string.IsNullOrWhiteSpace(PackageOutputFolder) ? PackageOutputFolder
            : !string.IsNullOrWhiteSpace(PackageRootPath) && PackageRootPath != @"C:\Temp" ? PackageRootPath
            : @"C:\Temp\Auto Package";

        [JsonPropertyName("PSADTTemplateFolder")]
        public string TemplateFolderPath    { get; set; } = "";

        [JsonPropertyName("CreateMST")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool CreateMST               { get; set; } = false;

        [JsonPropertyName("RemoveDesktopShortcut")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool RemoveDesktopShortcut   { get; set; } = false;

        [JsonPropertyName("CreateIntunewin")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool CreateIntunewin         { get; set; } = false;

        [JsonPropertyName("PublishToIntune")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool PublishToIntune         { get; set; } = false;

        [JsonPropertyName("AssignToGroup")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AssignToGroup           { get; set; } = false;

        [JsonPropertyName("InstallPackage")]
        public string InstallPackage        { get; set; } = "No"; // "No" / "Administrator" / "System Context"

        [JsonPropertyName("UninstallPackage")]
        public string UninstallPackage      { get; set; } = "No"; // "No" / "Administrator" / "System Context"

        [JsonPropertyName("CreateDocument")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool CreateDocument          { get; set; } = false;

        // Document copy flags (global AutoPackage.json). "Yes"/"No".
        [JsonPropertyName("CopytxtToDocuments")]
        public string CopytxtToDocuments    { get; set; } = "No";
        [JsonPropertyName("CopydocxToDocuments")]
        public string CopydocxToDocuments   { get; set; } = "No";
        [JsonPropertyName("CopyLogsToDocuments")]
        public string CopyLogsToDocuments   { get; set; } = "No";

        [JsonPropertyName("InstallBehaviour")]
        public string InstallBehaviour      { get; set; } = "System";

        [JsonPropertyName("DeployGroupName")]
        public string DeployGroupName       { get; set; } = "";

        [JsonPropertyName("MSTProperties")]
        public List<AutoMstProperty> MSTProperties { get; set; } = new();

        [JsonPropertyName("IntuneSettings")]
        public AutoIntuneSettings IntuneSettings { get; set; } = new();

        /// <summary>Full path to IntuneAuth.json containing TenantId, ClientId and optionally ClientSecret.
        /// Store in a secured/network location accessible only to IT admins.
        /// Example: \\\\secureserver\\IT\\SwiftPack\\IntuneAuth.json
        /// If empty, falls back to interactive browser login.</summary>
        [JsonPropertyName("IntuneAuthPath")]
        public string IntuneAuthPath { get; set; } = "";

        // ── Send-mail (App Owner UAT notification) settings ───────────────
        // MailMode: "Graph" (reuse Intune app registration + Mail.Send) or "SMTP".
        [JsonPropertyName("MailMode")]
        public string MailMode { get; set; } = "Graph";
        // The "from" mailbox. For Graph this is the /users/{sender}/sendMail mailbox.
        [JsonPropertyName("MailSenderAddress")]
        public string MailSenderAddress { get; set; } = "";
        // SMTP-only settings.
        [JsonPropertyName("SmtpServer")]
        public string SmtpServer { get; set; } = "smtp.office365.com";
        [JsonPropertyName("SmtpPort")]
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int SmtpPort { get; set; } = 587;
        // SMTP login username when it differs from the sender address (e.g. SMTP2GO,
        // SendGrid). Leave empty for Office365/Gmail where the sender address is the login.
        [JsonPropertyName("SmtpUsername")]
        public string SmtpUsername { get; set; } = "";
        // App password, DPAPI-encrypted (Base64). Use the in-app helper to set this;
        // never store a plaintext password here.
        [JsonPropertyName("SmtpAppPasswordEncrypted")]
        public string SmtpAppPasswordEncrypted { get; set; } = "";
        // ── Customizable UAT-notification mail text ───────────────────────
        // MailSubject and MailBody support the tokens {AppName}, {AppVersion},
        // {AppVendor} and {PackageName}; empty means "use the built-in default".
        // MailBody is multi-line: blank lines separate paragraphs, single line
        // breaks render as <br/>. When MailBody is set it becomes the entire
        // message body; when empty a built-in default body is composed.
        [JsonPropertyName("MailSubject")]
        public string MailSubject { get; set; } = "";
        [JsonPropertyName("MailBody")]
        public string MailBody { get; set; } = "";
        // ── Per-file-type command injection flags ─────────────────────────
        // When "Yes", the corresponding file type is processed and commands
        // are injected into Invoke-AppDeployToolkit.ps1. Default = Yes.
        [JsonPropertyName("AddMsiCommandToPs1")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddMsiCommandToPs1 { get; set; } = true;

        [JsonPropertyName("AddMstCommandToPs1")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddMstCommandToPs1 { get; set; } = true;

        [JsonPropertyName("AddMspCommandToPs1")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddMspCommandToPs1 { get; set; } = true;

        [JsonPropertyName("AddExeCommandToPs1")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddExeCommandToPs1 { get; set; } = true;

        [JsonPropertyName("AddBatCommandToPs1")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddBatCommandToPs1 { get; set; } = true;

        [JsonPropertyName("AddIssCommandToPs1")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddIssCommandToPs1 { get; set; } = true;

        [JsonPropertyName("AddRegCommandToPs1")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddRegCommandToPs1 { get; set; } = true;

        [JsonPropertyName("AddCerCommandToPs1")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddCerCommandToPs1 { get; set; } = true;

        [JsonPropertyName("AddInfCommandToPs1")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddInfCommandToPs1 { get; set; } = true;

        // ── ARP flags — if "Yes", inject Set-ADTRegistryKey for NoModify/NoRepair/NoRemove ─
        // after ## <Perform Post-Installation tasks here> when not already in uninstall key.
        // Default = false (opt-in only).
        [JsonPropertyName("AddNoModify")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddNoModify { get; set; } = false;

        [JsonPropertyName("AddNoRepair")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddNoRepair { get; set; } = false;

        [JsonPropertyName("AddNoRemove")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddNoRemove { get; set; } = false;

        [JsonPropertyName("AddInstallName")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddInstallName { get; set; } = false;

        [JsonPropertyName("AddInstallTitle")]
        [JsonConverter(typeof(YesNoBoolConverter))]
        public bool AddInstallTitle { get; set; } = false;
    }

    internal class AutoMstProperty
    {
        [JsonPropertyName("Property")]
        public string Property { get; set; } = "";

        [JsonPropertyName("Value")]
        public string Value    { get; set; } = "";
    }

    /// <summary>
    /// Per-app Intune settings override — fields from the per-app JSON that take priority
    /// over the global IntuneSettings.json. Passed via SetStaticPendingMetadata.
    /// </summary>
    public class AutoIntuneSettingsOverride
    {
        public string InstallCommand             { get; set; } = "";
        public string UninstallCommand           { get; set; } = "";
        public string AllowAvailableUninstall    { get; set; } = "";
        public string InstallBehavior            { get; set; } = "";
        public string DeviceRestartBehaviour     { get; set; } = "";
        public string MinimumOperatingSystem     { get; set; } = "";
        public string OperatingSystemArchitecture { get; set; } = "";
        public string AssignmentsRequired        { get; set; } = "";
        public string AssignmentsAvailable       { get; set; } = "";
        public string AssignmentsUninstall       { get; set; } = "";
        public string RulesFormat                        { get; set; } = "Manual detection rules";
        public string DetectionTypeMSI                   { get; set; } = "No";
        public string DetectionTypeFile                  { get; set; } = "No";
        public string DetectionTypeRegistry              { get; set; } = "No";
        public string DetectionTypeScript                { get; set; } = "No";
        public string DetectionMsiVersionCheck           { get; set; } = "";
        public string DetectionMsiOperator               { get; set; } = "";
        public string DetectionFileFolderPath            { get; set; } = "";
        public string DetectionFileFileName              { get; set; } = "";
        public string DetectionFileDetectionMethod             { get; set; } = "";
        public string DetectionFileOperator              { get; set; } = "";
        public string DetectionFileValue                 { get; set; } = "";
        public string DetectionFileSizeValue             { get; set; } = "";
        public string DetectionFileDateValue             { get; set; } = "";
        public string DetectionFileTimeValue             { get; set; } = "";
        public string DetectionFile32Bit                 { get; set; } = "";
        public string DetectionRegKeyPath                { get; set; } = "";
        public string DetectionRegValueName              { get; set; } = "";
        public string DetectionRegDetectionMethod              { get; set; } = "";
        public string DetectionRegOperator               { get; set; } = "";
        public string DetectionReg32Bit                  { get; set; } = "";
        public string DetectionScriptFile                { get; set; } = "";
        public string DetectionScriptEnforceSignatureCheck { get; set; } = "";
        public string DetectionScriptRunAs32Bit          { get; set; } = "";
        public string Developer                  { get; set; } = "";
        public string Owner                      { get; set; } = "";
        public string FeaturedApp                { get; set; } = "";
        public string Category                   { get; set; } = ""; // from per-app "IntuneCategory"
        public string Description                { get; set; } = "";
        // Intune App Info overrides — displayed name/vendor/version in Intune (may differ from package)
        public string IntuneAppName              { get; set; } = "";
        public string IntuneAppVendor            { get; set; } = "";
        public string IntuneAppVersion           { get; set; } = "";
        // App Info tab
        public string InformationURL             { get; set; } = "";
        public string PrivacyURL                 { get; set; } = "";
        public string Notes                      { get; set; } = "";
        public string Logo                       { get; set; } = "";
        // Program tab
        public string InstallationTime           { get; set; } = "";
        // Assignments tab
        public string UATUsers                   { get; set; } = "";
    }

    public class AutoIntuneSettings
    {
        // ── Authentication ────────────────────────────────────────────────────
        // TenantId — always read from AutoPackage.json (single source of truth)
        [JsonPropertyName("TenantId")]
        public string TenantId { get; set; } = "";

        // ClientId — read from AutoPackage.json; overridable via Credential Manager
        [JsonPropertyName("ClientId")]
        public string ClientId { get; set; } = "";

        // ── App Info ─────────────────────────────────────────────────────────
        [JsonPropertyName("Publisher")]       // legacy key kept for back-compat
        public string Publisher { get; set; } = "";
        // Intune display name/vendor/version — may differ from package metadata
        [JsonPropertyName("IntuneAppName")]
        public string IntuneAppName { get; set; } = "";
        [JsonPropertyName("IntuneAppVendor")]
        public string IntuneAppVendor { get; set; } = "";
        [JsonPropertyName("IntuneAppVersion")]
        public string IntuneAppVersion { get; set; } = "";
        // App Info tab extras
        [JsonPropertyName("InformationURL")]
        public string InformationURL { get; set; } = "";
        [JsonPropertyName("PrivacyURL")]
        public string PrivacyURL { get; set; } = "";
        [JsonPropertyName("Notes")]
        public string Notes { get; set; } = "";
        [JsonPropertyName("Logo")]
        public string Logo { get; set; } = "";
        // Program tab
        [JsonPropertyName("InstallationTime")]
        public string InstallationTime { get; set; } = "";
        // Assignments tab
        [JsonPropertyName("UATUsers")]
        public string UATUsers { get; set; } = "";

        [JsonPropertyName("Description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("Category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("Developer")]       // new key (was DefaultDeveloper)
        public string Developer { get; set; } = "";

        [JsonPropertyName("Owner")]           // new key (was DefaultOwner)
        public string Owner { get; set; } = "";

        [JsonPropertyName("FeaturedApp")]     // new key (was DefaultFeatured)
        public string FeaturedApp { get; set; } = "No";

        // ── Program ──────────────────────────────────────────────────────────
        [JsonPropertyName("InstallCommand")]
        public string InstallCommand { get; set; } = "";

        [JsonPropertyName("UninstallCommand")]
        public string UninstallCommand { get; set; } = "";

        [JsonPropertyName("AllowAvailableUninstall")]
        public string AllowAvailableUninstall { get; set; } = "No";

        [JsonPropertyName("InstallBehavior")]      // new key (was InstallExperience)
        public string InstallBehavior { get; set; } = "System";

        [JsonPropertyName("InstallExperience")]    // legacy - overridden by InstallBehavior if set
        public string InstallExperience { get; set; } = "";

        [JsonPropertyName("DeviceRestartBehaviour")]  // new key (was RestartBehavior)
        public string DeviceRestartBehaviour { get; set; } = "";

        [JsonPropertyName("RestartBehavior")]      // legacy - overridden by DeviceRestartBehaviour if set
        public string RestartBehavior { get; set; } = "suppress";

        // ── Requirements ─────────────────────────────────────────────────────
        [JsonPropertyName("MinimumOperatingSystem")]  // new key (was MinimumOs)
        public string MinimumOperatingSystem { get; set; } = "";

        [JsonPropertyName("MinimumOs")]               // legacy
        public string MinimumOs { get; set; } = "Windows 10 21H2";

        [JsonPropertyName("OperatingSystemArchitecture")]  // new key (was Architecture list)
        public string OperatingSystemArchitecture { get; set; } = "";

        [JsonPropertyName("Architecture")]            // legacy list
        public List<string> Architecture { get; set; } = new() { "x64", "x86" };

        // ── Detection ────────────────────────────────────────────────────────
        [JsonPropertyName("Detection.Types")]
        public List<string> DetectionTypes { get; set; } = new();

        [JsonPropertyName("Detection.MSI.VersionCheck")]
        public string DetectionMsiVersionCheck { get; set; } = "No";

        [JsonPropertyName("Detection.MSI.Operator")]
        public string DetectionMsiOperator { get; set; } = "Equal to";

        [JsonPropertyName("Detection.File.FolderPath")]
        public string DetectionFileFolderPath { get; set; } = "";

        [JsonPropertyName("Detection.File.FileName")]
        public string DetectionFileFileName { get; set; } = "";

        [JsonPropertyName("Detection.File.CheckType")]
        public string DetectionFileDetectionMethod { get; set; } = "File or folder exists";

        [JsonPropertyName("Detection.File.Operator")]
        public string DetectionFileOperator { get; set; } = "Equal to";

        [JsonPropertyName("Detection.File.SizeValue")]
        public string DetectionFileSizeValue { get; set; } = "";

        [JsonPropertyName("Detection.File.Check32BitAppOn64BitClients")]
        public string DetectionFile32Bit { get; set; } = "Uncheck";

        [JsonPropertyName("Detection.Registry.KeyPath")]
        public string DetectionRegKeyPath { get; set; } = "";

        [JsonPropertyName("Detection.Registry.ValueName")]
        public string DetectionRegValueName { get; set; } = "DisplayVersion";

        [JsonPropertyName("Detection.Registry.CheckType")]
        public string DetectionRegDetectionMethod { get; set; } = "Key exists";

        [JsonPropertyName("Detection.Registry.Operator")]
        public string DetectionRegOperator { get; set; } = "Equal to";

        [JsonPropertyName("Detection.Registry.Check32BitAppOn64BitClients")]
        public string DetectionReg32Bit { get; set; } = "Uncheck";

        [JsonPropertyName("Detection.Script.ScriptFile")]
        public string DetectionScriptFile { get; set; } = "";

        [JsonPropertyName("Detection.Script.EnforceScriptSignatureCheck")]
        public string DetectionScriptEnforceSignatureCheck { get; set; } = "No";

        [JsonPropertyName("Detection.Script.RunScriptAs32BitProcessOn64BitClient")]
        public string DetectionScriptRunAs32Bit { get; set; } = "No";

        // ── Assignments ──────────────────────────────────────────────────────
        [JsonPropertyName("Assignments.Required")]
        public string AssignmentsRequired { get; set; } = "";

        [JsonPropertyName("Assignments.Available")]
        public string AssignmentsAvailable { get; set; } = "";

        [JsonPropertyName("Assignments.Uninstall")]
        public string AssignmentsUninstall { get; set; } = "";

        // ── Resolved helpers (prefer new keys, fall back to legacy) ──────────
        [JsonIgnore]
        public string ResolvedInstallBehavior =>
            !string.IsNullOrWhiteSpace(InstallBehavior)
                ? InstallBehavior.Equals("User", StringComparison.OrdinalIgnoreCase) ? "user" : "system"
                : !string.IsNullOrWhiteSpace(InstallExperience) ? InstallExperience : "system";

        [JsonIgnore]
        public string ResolvedRestartBehavior =>
            !string.IsNullOrWhiteSpace(DeviceRestartBehaviour)
                ? MapRestartBehavior(DeviceRestartBehaviour)
                : !string.IsNullOrWhiteSpace(RestartBehavior) ? RestartBehavior : "suppress";

        [JsonIgnore]
        public string ResolvedMinimumOs =>
            !string.IsNullOrWhiteSpace(MinimumOperatingSystem)
                ? MinimumOperatingSystem
                : MinimumOs;

        private static string MapRestartBehavior(string friendly) => friendly switch
        {
            var s when s.Contains("No specific",        StringComparison.OrdinalIgnoreCase) => "suppress",
            var s when s.Contains("may force",          StringComparison.OrdinalIgnoreCase) => "basedOnReturnCode",
            var s when s.Contains("mandatory",          StringComparison.OrdinalIgnoreCase) => "force",
            _ => "basedOnReturnCode"
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    //  PAGE
    // ══════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Local mirror of DownloadSourceEntry — decouples AutoPackagePage from
    /// SourceDownloadPage at compile time. Covers every property accessed via
    /// perApp / p / js / sel / _staticSelectedApp throughout AutoPackagePage.
    /// Extra JSON fields are silently ignored during deserialization.
    /// </summary>
    public class AutoPackageAppEntry
    {
        // ── App Info ─────────────────────────────────────────────────────────
        public string Description                          { get; set; } = "";
        public string Developer                            { get; set; } = "";
        public string Owner                                { get; set; } = "";
        public string InformationURL                       { get; set; } = "";
        public string PrivacyURL                           { get; set; } = "";
        public string Notes                                { get; set; } = "";
        public string Logo                                 { get; set; } = "";
        public string IntuneCategory                       { get; set; } = "";
        public string FeaturedApp                          { get; set; } = "";
        public string IntuneAppName                        { get; set; } = "";
        public string IntuneAppVendor                      { get; set; } = "";
        public string IntuneAppVersion                     { get; set; } = "";

        // ── Program ──────────────────────────────────────────────────────────
        public string InstallCommand                       { get; set; } = "";
        public string UninstallCommand                     { get; set; } = "";
        public string AllowAvailableUninstall              { get; set; } = "";
        public string InstallBehavior                      { get; set; } = "";
        public string InstallationTime                     { get; set; } = "";
        public string DeviceRestartBehaviour               { get; set; } = "";

        // ── Requirements ─────────────────────────────────────────────────────
        public string MinimumOperatingSystem               { get; set; } = "";
        public string OperatingSystemArchitecture          { get; set; } = "";

        // ── Detection ────────────────────────────────────────────────────────
        public string RulesFormat                          { get; set; } = "";
        public string DetectionTypeMSI                     { get; set; } = "No";
        public string DetectionTypeFile                    { get; set; } = "No";
        public string DetectionTypeRegistry                { get; set; } = "No";
        public string DetectionTypeScript                  { get; set; } = "No";
        public string DetectionMsiVersionCheck             { get; set; } = "";
        public string DetectionMsiOperator                 { get; set; } = "";
        public string DetectionMsiValue                    { get; set; } = "";
        public string DetectionMsiProductCode              { get; set; } = "";
        public string DetectionFileFolderPath              { get; set; } = "";
        public string DetectionFileFileName                { get; set; } = "";
        public string DetectionFileDetectionMethod               { get; set; } = "";
        public string DetectionFileOperator                { get; set; } = "";
        public string DetectionFileValue                   { get; set; } = "";
        // Size (in MB) value used when DetectionFileDetectionMethod == "Size in MB".
        // Kept separate from DetectionFileValue (which holds the version for
        // "String (Version)") so the version no longer leaks into the Size rule.
        public string DetectionFileSizeValue               { get; set; } = "";
        public string DetectionFileDateValue               { get; set; } = "";
        public string DetectionFileTimeValue               { get; set; } = "";
        public string DetectionFile32Bit                   { get; set; } = "";
        public string DetectionRegKeyPath                  { get; set; } = "";
        public string DetectionRegValueName                { get; set; } = "";
        public string DetectionRegDetectionMethod                { get; set; } = "";
        public string DetectionRegOperator                 { get; set; } = "";
        public string DetectionRegistryValue               { get; set; } = "";
        public string DetectionReg32Bit                    { get; set; } = "";
        public string DetectionScriptFile                  { get; set; } = "";
        public string DetectionScriptEnforceSignatureCheck { get; set; } = "";
        public string DetectionScriptRunAs32Bit            { get; set; } = "";

        // ── Assignments ───────────────────────────────────────────────────────
        public string DeploymentType                       { get; set; } = "";
        public string AssignmentsRequired                  { get; set; } = "";
        public string AssignmentsAvailable                 { get; set; } = "";
        public string AssignmentsUninstall                 { get; set; } = "";
        public string UATUsers                             { get; set; } = "";
        /// <summary>App owner email address(es) for the UAT notification mail.
        /// Multiple addresses separated by ';' (e.g. "a@x.com; b@y.com").</summary>
        public string AppOwnerEmail                        { get; set; } = "";
        /// <summary>"Yes" → send the UAT notification mail to AppOwnerEmail after publish.</summary>
        public string SendMailToAppOwner                   { get; set; } = "";
        /// <summary>Per-app mail wording overrides; empty falls back to AutoPackage.json.</summary>
        public string MailSubject                          { get; set; } = "";
        public string MailBody                             { get; set; } = "";
        public string CopytxtToDocuments                   { get; set; } = "No";
        public string CopydocxToDocuments                  { get; set; } = "No";

        // ── Dependencies ──────────────────────────────────────────────────────
        public string Dependency1                          { get; set; } = "";
        public string AutoInstallDependency1               { get; set; } = "No";
        public string Dependency2                          { get; set; } = "";
        public string AutoInstallDependency2               { get; set; } = "No";
        public string Dependency3                          { get; set; } = "";
        public string AutoInstallDependency3               { get; set; } = "No";
        public string Dependency4                          { get; set; } = "";
        public string AutoInstallDependency4               { get; set; } = "No";

        // ── Supersedences ─────────────────────────────────────────────────────
        public string Supersedence1                        { get; set; } = "";
        public string UninstallPreviousVersion1            { get; set; } = "No";
        public string Supersedence2                        { get; set; } = "";
        public string UninstallPreviousVersion2            { get; set; } = "No";
        public string Supersedence3                        { get; set; } = "";
        public string UninstallPreviousVersion3            { get; set; } = "No";
        public string Supersedence4                        { get; set; } = "";
        public string UninstallPreviousVersion4            { get; set; } = "No";

        // Dynamic dependency / supersedence lists (carried from the per-app entry; App ID based).
        public List<DependencyItem>   Dependencies  { get; set; } = new();
        public List<SupersedenceItem> Supersedences { get; set; } = new();

        /// <summary>"Yes" lets the publish script supersede the previous version of this app on its
        /// own; "No" sends only the supersedences above. Blank counts as Yes.</summary>
        public string AddSupersedenceAutomatically         { get; set; } = "Yes";

        // ── Silent parameters / uninstall ─────────────────────────────────────
        public string SilentInstallParameterEXE            { get; set; } = "";
        public string SilentUninstallParameterEXE          { get; set; } = "";
        public string SilentInstallParameterMSI            { get; set; } = "";
        public string SilentUninstallParameterMSI          { get; set; } = "";
        public string UninstallPath                        { get; set; } = "";

        // ── PSADT template + commands (used to decide Program-tab Install/Uninstall) ──
        /// <summary>When set, a PSADT template drives packaging; the PSADT*Command values
        /// are used for the Intune Program tab. When empty, commands are derived from the
        /// installer + silent parameters (or InstallCommand/UninstallCommand if present).</summary>
        public string PSADTTemplateFolder                  { get; set; } = "";
        public string PSADTInstallCommand                  { get; set; } = "";
        public string PSADTUninstallCommand                { get; set; } = "";

        // ── Source (non-PSADT) commands — explicit overrides used when there is NO PSADT
        //    template. Distinct from InstallCommand/UninstallCommand (the FINAL Intune
        //    Program-tab values that get written to the output JSON) to avoid the
        //    self-referential conflict between source and derived values.
        public string SourceInstallCommand                 { get; set; } = "";
        public string SourceUninstallCommand               { get; set; } = "";

        /// <summary>"MSI_x64" | "MSI_x86" | "EXE_x64" | "EXE_x86" — which installer kind was chosen.</summary>
        public string DownloadPreference                   { get; set; } = "";

        // ── Install / Uninstall behaviour ────────────────────────────────────────
        /// <summary>"Yes" / "Administrator" / "System Context" / "No" — triggers Execute Install after package creation.</summary>
        public string InstallPackage                       { get; set; } = "";
        /// <summary>"Yes" / "Administrator" / "System Context" / "No" — triggers Execute Uninstall (5 s after Install completes).</summary>
        public string UninstallPackage                     { get; set; } = "";
        /// <summary>"Yes" -> after a successful Execute Install, open the app's main exe
        /// (resolved from a Desktop / Start Menu shortcut), hold it briefly, then close it
        /// before the next step. Default "No".</summary>
        public string OpenApplication                      { get; set; } = "No";

        // ── Source app identity (for registry lookup) ─────────────────────────
        /// <summary>Actual installer display name (e.g. "Smekal FileCompare") — used to look up
        /// the Windows uninstall registry entry. Distinct from IntuneAppName ("FileCompare 1")
        /// which rarely matches the registry DisplayName.</summary>
        public string SourceAppName                        { get; set; } = "";
        public string SourceAppVendor                      { get; set; } = "";
        public string SourceAppVersion                     { get; set; } = "";

        // ── Intunewin / Publish flags (per-app override) ──────────────────────
        /// <summary>"Yes" / "No" — per-app override for CreateIntunewin. Empty means defer to global AutoPackage.json.</summary>
        public string CreateIntunewin                      { get; set; } = "";
        /// <summary>"Yes" / "No" — per-app override for PublishToIntune. Empty means defer to global AutoPackage.json.</summary>
        public string PublishToIntune                      { get; set; } = "";
        /// <summary>"Yes" / "No" — navigate to Publish tab + write Intune JSON after Intunewin completes.
        /// Distinct from PublishToIntune: generates and saves the JSON but does not push to Intune.</summary>
        public string PublishSettings                      { get; set; } = "";
    }

    public partial class AutoPackagePage : Page
    {
        // ── Decoupled from SourceDownloadPage — local copy of GlobalShowMessageBox ──
        private static bool _localShowMessageBox = true;

        // ── Config file path ──────────────────────────────────────────────
        private static readonly string ConfigPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Smekal", "SwiftPack Studio", "Templates", "AutoPackage.json");

        private static readonly string IntuneWinAppUtilPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Smekal", "SwiftPack Studio", "Templates", "IntuneWinAppUtil.exe");

        private static readonly string IntuneSettingsPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Smekal", "SwiftPack Studio", "Templates", "IntuneSettings.json");

        private static readonly string PublishScriptPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Smekal", "SwiftPack Studio", "Templates", "AutoPackage_PublishToIntune.ps1");

        // ── State ─────────────────────────────────────────────────────────
        private bool _initialized = false;    // true after first OnLoaded - prevents data clear on re-navigation
        private AutoPackageConfig _cfg  = new();
        private string _msiPath         = "";          // resolved MSI file (may be inside source folder)
        private volatile bool _sourceFullyLoaded = false; // set when MSI props or EXE info is applied
        private string _pendingInstallArgs   = ""; // set by SetInstallUninstallArgs, applied in TriggerAutoCreate
        private string _jsonInstallArgs      = ""; // SilentInstallParameterEXE from per-app JSON - persists for ExeRebuildCommands
        private string _pendingUninstallArgs = "";
        private string _pendingMsiInstallArgs   = ""; // SilentInstallParameterMSI from per-app JSON
        private string _pendingMsiUninstallArgs = ""; // SilentUninstallParameterMSI from per-app JSON
        private string _pendingDeploymentType   = ""; // DeploymentType from per-app JSON
        private string _pendingDetectionType    = ""; // DetectionType from per-app JSON (legacy)
        private string _pendingDetTypeMSI       = "";
        private string _pendingDetTypeFile      = "";
        private string _pendingDetTypeRegistry  = "";
        // Captured detection type booleans for Publish tab checkbox sync
        private bool _publishDetWantMsi  = true;
        private bool _publishDetWantFile = true;
        private bool _publishDetWantReg  = true;
        private string _pendingDetTypeScript    = "";
        private string _pendingRulesFormat      = "";
        private string _pendingDescription      = ""; // Description from per-app JSON
        private string _pendingGroupName        = ""; // GroupName from per-app JSON
        private string _pendingDependency1      = "";
        private string _pendingDependency2      = "";
        private string _pendingDependency3      = "";
        private string _pendingDependency4      = "";
        private string _pendingSupersedence1    = "";
        private string _pendingSupersedence2    = "";
        private string _pendingSupersedence3    = "";
        private string _pendingSupersedence4    = "";
        private string _pendingAppVendor     = ""; // set by SetPackageMetadata, applied in TriggerAutoCreate
        private string _pendingAppName       = "";
        private string _pendingCloseApps     = "";
        private string _pendingAppVersion    = ""; // from AppVersion in SourceDownload.json
        private string _pendingUninstallPath = ""; // from SourceUninstallPath in SourceDownload.json
        private string _pendingCreateIntunewin = ""; // from CreateIntunewin in per-app JSON
        private string _pendingPublishToIntune  = ""; // from PublishToIntune in per-app JSON
        private string _pendingAssignToGroup    = ""; // from AssignToGroup in per-app JSON
        private string _pendingTemplateFolder = ""; // set by SetPackageTemplateFolder, applied after folder creation
        private string _autoLogoPerAppFolder  = ""; // per-app folder (App Settings\<Vendor>\<AppName>) for the auto-flow Icon.png
        private string _pendingTemplateFolderUI = ""; // set from PSADTTemplatePath, applied to TxtTemplateFolder in TriggerAutoCreate
        private bool   _appTemplateFolderEnabled = false; // true only when TemplateFolder is set in app JSON

        // ── Static pending store — survives navigation, picked up by TriggerAutoCreate ──
        private static string _staticPendingTemplateFolder  = "";
        private static bool   _staticPendingAppTemplateFolderEnabled = false; // true when TemplateFolder is non-empty in app JSON
        private static string _staticPendingCreateIntunewin = "";
        private static string _staticPendingPublishToIntune = "";
        private static string _staticPendingAssignToGroup   = "";
        private static string _staticPendingMsiInstallArgs  = "";
        private static string _staticPendingMsiUninstallArgs = "";
        private static string _staticPendingDeploymentType  = "";
        private static string _staticPendingDetectionType   = "";
        private static string _staticPendingDetTypeMSI      = "";
        private static string _staticPendingDetTypeFile     = "";
        private static string _staticPendingDetTypeRegistry = "";
        private static string _staticPendingDetTypeScript   = "";
        private static string _staticPendingRulesFormat     = "";
        private static string _staticPendingDescription     = "";
        private static string _staticPendingGroupName       = "";
        private static string _staticPendingDependency1            = "";
        private static string _staticPendingAutoInstallDependency1  = "No";
        private static string _staticPendingDependency2            = "";
        private static string _staticPendingAutoInstallDependency2  = "No";
        private static string _staticPendingDependency3            = "";
        private static string _staticPendingAutoInstallDependency3  = "No";
        private static string _staticPendingDependency4            = "";
        private static string _staticPendingAutoInstallDependency4  = "No";
        private static string _staticPendingSupersedence1          = "";
        private static string _staticPendingUninstallPreviousVersion1 = "No";
        private static string _staticPendingSupersedence2          = "";
        private static string _staticPendingUninstallPreviousVersion2 = "No";
        private static string _staticPendingSupersedence3          = "";
        private static string _staticPendingUninstallPreviousVersion3 = "No";
        private static string _staticPendingSupersedence4          = "";
        private static string _staticPendingUninstallPreviousVersion4 = "No";
        private static string _staticPendingPackageOutputFolder = "";
        private static string _staticPendingCreateMst            = "";
        private static string _staticPendingRemoveDesktopShortcut = "";
        private static string _staticPendingPackageTemplateFolder  = "";
        private static string _staticPendingInstallPackage         = "";
        private static string _staticPendingUninstallPackage       = "";
        private static string _staticPendingAddInstallName         = "";
        private static string _staticPendingAddInstallTitle        = "";
        private static string _staticPendingAddNoModify            = "";
        private static string _staticPendingAddNoRepair            = "";
        private static string _staticPendingAddNoRemove            = "";
        // Per-app Intune overrides — applied to _cfg.IntuneSettings in TriggerAutoCreate
        private static AutoIntuneSettingsOverride? _staticPendingIntuneOverrides = null;
        private static AutoPackageAppEntry? _staticSelectedApp = null;
        // Read-only access so SourceDownloadPage's Intune settings tab can show the source
        // Install/Uninstall command that a no-template Create Package stored here.
        public static AutoPackageAppEntry? StaticSelectedApp => _staticSelectedApp;

        /// <summary>Called from SourceDownloadPage before navigation — stores values statically so
        /// TriggerAutoCreate can pick them up regardless of timing.</summary>
        public static void SetStaticPendingMetadata(
            string templateFolder = "", string createIntunewin = "", string publishToIntune = "",
            string assignToGroup   = "", string msiInstallArgs = "", string msiUninstallArgs = "",
            string deploymentType = "", string detectionType = "", string description = "", string groupName = "",
            string packageOutputFolder = "", string createMst = "", string removeDesktopShortcut = "",
            string packageTemplateFolder = "", string installPackage = "", string uninstallPackage = "",
            string addInstallName = "", string addInstallTitle = "",
            string addNoModify = "", string addNoRepair = "", string addNoRemove = "",
            string dependency1 = "", string autoInstallDependency1 = "No",
            string dependency2 = "", string autoInstallDependency2 = "No",
            string dependency3 = "", string autoInstallDependency3 = "No",
            string dependency4 = "", string autoInstallDependency4 = "No",
            string supersedence1 = "", string uninstallPreviousVersion1 = "No",
            string supersedence2 = "", string uninstallPreviousVersion2 = "No",
            string supersedence3 = "", string uninstallPreviousVersion3 = "No",
            string supersedence4 = "", string uninstallPreviousVersion4 = "No",
            AutoIntuneSettingsOverride? intuneOverrides = null)
        {
            _staticPendingTemplateFolder         = templateFolder;
            _staticPendingAppTemplateFolderEnabled = !string.IsNullOrWhiteSpace(templateFolder);
            _staticPendingCreateIntunewin        = createIntunewin;
            _staticPendingPublishToIntune        = publishToIntune;
            // The 'publishToIntune' parameter actually carries the "Publish settings" choice
            // (it drives navigation to the Publish tab + auto-Save, which writes the JSON).
            _autoTriggerSave                     = publishToIntune.Equals("Yes", StringComparison.OrdinalIgnoreCase);
            // The actual upload to Intune must be gated by the REAL "Publish to intune" checkbox
            // (_staticSelectedApp.PublishToIntune), NOT by "Publish settings". Otherwise enabling
            // Publish settings alone would wrongly push the app to Intune.
            _autoTriggerPublish                  = _staticSelectedApp?.PublishToIntune == "Yes";
            _staticPendingAssignToGroup          = assignToGroup;
            _staticPendingMsiInstallArgs         = msiInstallArgs;
            _staticPendingMsiUninstallArgs       = msiUninstallArgs;
            _staticPendingDeploymentType         = deploymentType;
            _staticPendingDetectionType          = detectionType;
            _staticPendingDetTypeMSI             = intuneOverrides?.DetectionTypeMSI      ?? "";
            _staticPendingDetTypeFile            = intuneOverrides?.DetectionTypeFile     ?? "";
            _staticPendingDetTypeRegistry        = intuneOverrides?.DetectionTypeRegistry ?? "";
            _staticPendingDetTypeScript          = intuneOverrides?.DetectionTypeScript   ?? "";
            _staticPendingRulesFormat            = intuneOverrides?.RulesFormat           ?? "";
            _staticPendingDescription            = description;
            _staticPendingGroupName              = groupName;
            _staticPendingDependency1               = dependency1;
            _staticPendingAutoInstallDependency1    = autoInstallDependency1;
            _staticPendingDependency2               = dependency2;
            _staticPendingAutoInstallDependency2    = autoInstallDependency2;
            _staticPendingDependency3               = dependency3;
            _staticPendingAutoInstallDependency3    = autoInstallDependency3;
            _staticPendingDependency4               = dependency4;
            _staticPendingAutoInstallDependency4    = autoInstallDependency4;
            _staticPendingSupersedence1             = supersedence1;
            _staticPendingUninstallPreviousVersion1 = uninstallPreviousVersion1;
            _staticPendingSupersedence2             = supersedence2;
            _staticPendingUninstallPreviousVersion2 = uninstallPreviousVersion2;
            _staticPendingSupersedence3             = supersedence3;
            _staticPendingUninstallPreviousVersion3 = uninstallPreviousVersion3;
            _staticPendingSupersedence4             = supersedence4;
            _staticPendingUninstallPreviousVersion4 = uninstallPreviousVersion4;
            _staticPendingPackageOutputFolder    = packageOutputFolder;
            _staticPendingCreateMst              = createMst;
            _staticPendingRemoveDesktopShortcut  = removeDesktopShortcut;
            _staticPendingPackageTemplateFolder  = packageTemplateFolder;
            _staticPendingInstallPackage         = installPackage;
            _staticPendingUninstallPackage       = uninstallPackage;
            _staticPendingAddInstallName         = addInstallName;
            _staticPendingAddInstallTitle        = addInstallTitle;
            _staticPendingAddNoModify            = addNoModify;
            _staticPendingAddNoRepair            = addNoRepair;
            _staticPendingAddNoRemove            = addNoRemove;
            _staticPendingIntuneOverrides        = intuneOverrides;
            // Note: _staticSelectedApp is set separately via SetStaticSelectedApp() before this call
        }

        public static void SetStaticSelectedApp(AutoPackageAppEntry? app)
            => _staticSelectedApp = app;

        /// <summary>
        /// Called by SourceDownloadPage when "Uninstall source silently" is checked AND
        /// Intunewin/Publish is active. Stores the full silent uninstall command so
        /// publishAction can run it directly (no Execute tab UI) after Publish completes.
        /// Pass an empty string to clear.
        /// </summary>
        public static void SetStaticDeferUninstallSourceSilently(string uninstallCmd)
            => _staticDeferredUninstallSourceCmd = uninstallCmd;

        // Flag-only setter: tells publishAction to build the uninstall command lazily
        // from ExeTxtUninstallSource/Args AFTER install has run and LookupInstalledByExe
        // has populated those fields. Used when the uninstall path isn't known yet at
        // the time SourceDownloadPage fires (e.g. app not yet installed = not in registry).
        public static void SetStaticDeferUninstallSourceSilentlyLazy(string silentArgs)
        {
            _staticDeferredUninstallSourceCmd = "";          // clear any stale command
            _staticDeferredUninstallSourceLazy     = true;
            _staticDeferredUninstallSourceLazyArgs = silentArgs;
        }

        private Action? _onAfterCreateComplete = null; // fired after Auto Package Complete dialog closes

        // ── "All actions complete" notification ───────────────────────────────
        // Fired once when the entire auto-flow (Package → Execute → Intunewin →
        // Publish, as far as configured) has finished. SourceDownloadPage registers
        // here to re-enable its Download-Information / Package-Information columns,
        // which stay locked from the moment a download button is clicked until the
        // whole chain completes.
        private Action? _onAutoFlowAllComplete = null;
        private bool    _autoFlowCompleteRaised = false;

        /// <summary>Register a callback fired once when the whole auto-flow finishes.</summary>
        public void SetAutoFlowCompleteCallback(Action? cb)
        {
            _onAutoFlowAllComplete  = cb;
            _autoFlowCompleteRaised = false; // arm for the next run
        }

        /// <summary>Fire the all-complete callback exactly once per armed run.</summary>
        /// <summary>
        /// Called by the host when the silent run finished with no package chain to follow
        /// (e.g. only Install/Uninstall source were selected). Finalises the flow so the
        /// Download tab paints the per-action status.
        /// </summary>
        public void NotifyAutoFlowFinishedNoChain()
        {
            RaiseAutoFlowComplete();
        }

        /// <summary>
        /// Called when a chained step FAILS and the chain is aborted. Releases the
        /// auto-flow lock (unlocks the Download/Package columns and reactivates the
        /// top tabs) and paints the per-action status panel, but deliberately does
        /// NOT generate package documents or send the App Owner "ready for UAT"
        /// email — those are success-only side effects of a fully completed flow.
        /// </summary>
        public void AbortAutoFlow()
        {
            if (_autoFlowCompleteRaised) return;
            _autoFlowCompleteRaised = true;

            // Release the lock so the Download/Package columns and tabs become
            // interactive again immediately after the failure dialog is closed.
            var cb = _onAutoFlowAllComplete;
            if (cb != null)
            {
                try { Dispatcher.Invoke(cb); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }

            // Paint the per-action status panel (records show the failed step) without
            // running the success-only document/email generation.
            _documentSuppressed = false;
            try { ComputeAndPublishAutoFlowStatus(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        private void RaiseAutoFlowComplete()
        {
            if (_autoFlowCompleteRaised) return;
            _autoFlowCompleteRaised = true;

            // The whole flow is finished: release the sub-tab lock and return to the Download
            // tab NOW, on the UI thread, before the background document / mail task below runs.
            // Previously both of these happened only at the tail of that task (inside
            // ComputeAndPublishAutoFlowStatus), so a fault or stall in document generation or the
            // App Owner mail left AutoFlowStatus.FlowRunning stuck true forever - which froze the
            // six sub-tabs on the Execute tab after Uninstall package and never switched back to
            // Download. Releasing here is safe: that background work finishes well under the
            // scheduler's poll interval, so per-app serialisation still holds. The tail's
            // ComputeAndPublishAutoFlowStatus still runs (Finish is idempotent) to paint the
            // per-action checklist once the SendMail result is known.
            void ReturnToDownloadUnlocked()
            {
                try { AutoFlowStatus.FlowRunning = false; } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                try { ActivateTopTab(activeTab: "Download"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            if (Dispatcher.CheckAccess()) ReturnToDownloadUnlocked();
            else Dispatcher.Invoke(ReturnToDownloadUnlocked);

            // The whole auto-flow is done now — the Intune JSON exists and the app's install
            // state is settled, so this is the right moment to build the package document.
            try
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    GeneratePackageDocuments(s =>
                    {
                        try { if (!string.IsNullOrWhiteSpace(_logPath)) File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    });
                    // Enforce CopyLogsToDocuments (global AutoPackage.json): remove the PSADT
                    // .log files from the Documents folder unless it is set to "Yes". Runs
                    // before the App Owner mail so suppressed logs are not attached.
                    EnforceCopyLogsToDocuments(s =>
                    {
                        try { if (!string.IsNullOrWhiteSpace(_logPath)) File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    });
                    // Always refresh the on-screen Document tab with the final data, even when
                    // .txt/.docx generation is disabled.
                    _documentSuppressed = false;
                    try { Dispatcher.Invoke(() => RefreshDocumentTab()); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                    // Documents are written now — safe to attach them to the App Owner email.
                    try { SendAppOwnerMail(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                    // Finally, compute + publish the per-action status (includes SendMail result).
                    try { ComputeAndPublishAutoFlowStatus(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                });
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

            var cb = _onAutoFlowAllComplete;
            if (cb != null)
            {
                try { Dispatcher.Invoke(cb); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
        }

        /// <summary>
        /// The flow steps record their own outcome in AutoFlowStatus as they run. Here we only
        /// finalise and ask the Download tab to paint the result panel.
        /// </summary>
        private void ComputeAndPublishAutoFlowStatus()
        {
            string pkgName = "";
            try { pkgName = TxtPackageName?.Text?.Trim() ?? ""; } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            AutoFlowStatus.SetPackageName(pkgName);
            AutoFlowStatus.Finish();

            // Notify the Download tab to paint the status panel.
            try { Dispatcher.Invoke(() => _onAutoFlowStatusReady?.Invoke()); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        // Callback the Download tab registers to be told when status is ready to paint.
        private Action? _onAutoFlowStatusReady = null;
        public void SetAutoFlowStatusReadyCallback(Action? cb) => _onAutoFlowStatusReady = cb;

        // ── App Owner UAT email ───────────────────────────────────────────
        /// <summary>
        /// Reads the per-app Intune JSON; if SendMailToAppOwner=Yes and AppOwnerEmail is set,
        /// composes the UAT notification and sends it via AutoPackage_SendMail.ps1 (Graph or SMTP
        /// per AutoPackage.json). Records the SendMail action status. Skips (with a log line) when
        /// required body fields (UATUsers / assignment group) are missing.
        /// </summary>
        private void SendAppOwnerMail()
        {
            void Log(string s)
            {
                try { if (!string.IsNullOrWhiteSpace(_logPath)) File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }

            // Only act if the SendMail action was selected for this run.
            if (!AutoFlowStatus.IsWanted(AutoFlowStatus.SendMail)) return;

            try
            {
                // 1) Resolve the per-app Intune JSON.
                string jsonPath = _docIntuneJsonPath ?? "";
                if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
                {
                    Log("Send mail: per-app Intune JSON not found - skipping email.");
                    AutoFlowStatus.Set(AutoFlowStatus.SendMail, AutoFlowStatus.State.Failed);
                    return;
                }

                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = doc.RootElement;
                string J(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? (v.GetString() ?? "") : "";

                string sendFlag    = J("SendMailToAppOwner");
                string ownerEmail  = J("AppOwnerEmail");
                string appName     = J("IntuneAppName");
                string appVersion  = J("IntuneAppVersion");
                string appVendor   = J("IntuneAppVendor");
                string pkgName     = J("PackageName");
                if (string.IsNullOrWhiteSpace(pkgName))
                {
                    try { pkgName = TxtPackageName?.Text?.Trim() ?? ""; } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
                // Final fallback: the per-app Intune json is named after the package
                // (e.g. Smekal_FileCompare_1.0_01.json) — use that.
                if (string.IsNullOrWhiteSpace(pkgName))
                    pkgName = IOPath.GetFileNameWithoutExtension(jsonPath);

                // 2) Gate: must be opted-in and have at least one recipient.
                if (!sendFlag.Equals("Yes", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(ownerEmail))
                {
                    Log("Send mail: not enabled or no AppOwnerEmail - skipping.");
                    return; // not a failure - simply not requested for this app
                }

                // 2b) Gate: never notify the app owner when the flow itself failed.
                //     A UAT mail announces a successful deployment; if Publish-to-Intune
                //     (or any earlier selected action) failed, sending it would be wrong.
                {
                    string[] gateActions =
                    {
                        AutoFlowStatus.InstallSource,  AutoFlowStatus.UninstallSource,
                        AutoFlowStatus.CreatePackage,  AutoFlowStatus.InstallPackage,
                        AutoFlowStatus.UninstallPackage, AutoFlowStatus.CreateIntunewin,
                        AutoFlowStatus.PublishSettings, AutoFlowStatus.PublishToIntune,
                    };
                    var failed = gateActions.Where(a =>
                        AutoFlowStatus.Get(a) == AutoFlowStatus.State.Failed).ToList();
                    // Publish-to-Intune is the action the mail is really about — if it was
                    // selected for this run but never reported success, do not send either.
                    if (AutoFlowStatus.IsWanted(AutoFlowStatus.PublishToIntune) &&
                        AutoFlowStatus.Get(AutoFlowStatus.PublishToIntune) != AutoFlowStatus.State.Ok &&
                        !failed.Contains(AutoFlowStatus.PublishToIntune))
                        failed.Add(AutoFlowStatus.PublishToIntune);
                    if (failed.Count > 0)
                    {
                        Log($"Send mail: SKIPPED - prior action(s) failed or did not complete: {string.Join(", ", failed)}.");
                        AutoFlowStatus.Set(AutoFlowStatus.SendMail, AutoFlowStatus.State.Failed);
                        return;
                    }
                }

                // 3) Assignments are NOT required for the mail: the body is either the
                //    user's own MailBody text or intentionally empty, so there is nothing
                //    assignment-specific left to compose.

                // 4) Find attachments (.txt / .docx) in the package Documents folder.
                string pkgRoot = !string.IsNullOrWhiteSpace(_docPackageLocation) ? _docPackageLocation : "";
                var attachments = new List<string>();
                if (!string.IsNullOrWhiteSpace(pkgRoot))
                {
                    string docsDir = IOPath.Combine(ResolveOutputBase(pkgRoot), "Documents");
                    if (Directory.Exists(docsDir))
                    {
                        foreach (var pat in new[] { "*.txt", "*.docx" })
                            attachments.AddRange(Directory.GetFiles(docsDir, pat));
                    }
                }

                // 5) Compose subject + HTML body. Both are customizable from
                // AutoPackage.json (Settings window > Mail): MailSubject and MailBody.
                // Both support the tokens {AppName}, {AppVersion}, {AppVendor} and
                // {PackageName}. Empty keys fall back to the built-in defaults. MailBody
                // is multi-line: blank lines separate paragraphs, single line breaks
                // render as <br/>; when set it becomes the ENTIRE message body. Read live
                // values first (the Settings window saves straight to the file, so the
                // _cfg snapshot may be stale - same pattern as the mode/sender read
                // below).
                string liveSubjectTpl = "", liveBodyTpl = "";
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        using var mailDoc = JsonDocument.Parse(File.ReadAllText(ConfigPath),
                            new JsonDocumentOptions
                            {
                                CommentHandling     = JsonCommentHandling.Skip,
                                AllowTrailingCommas = true,
                            });
                        string MStr(string key) =>
                            mailDoc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                                ? (v.GetString() ?? "") : "";
                        liveSubjectTpl = MStr("MailSubject");
                        liveBodyTpl    = MStr("MailBody");
                    }
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                string Expand(string s) => (s ?? "")
                    .Replace("{AppName}", appName ?? "")
                    .Replace("{AppVersion}", appVersion ?? "")
                    .Replace("{AppVendor}", appVendor ?? "")
                    .Replace("{PackageName}", pkgName ?? "");
                bool HasToken(string s) => (s ?? "").Contains("{AppName}") || (s ?? "").Contains("{AppVersion}")
                                        || (s ?? "").Contains("{AppVendor}") || (s ?? "").Contains("{PackageName}");
                string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

                // Subject: a custom template without any token gets the app identity
                // appended automatically, so "App Deploy -" becomes
                // "App Deploy - 7zip 7-Zip 26.02" without the user writing tokens.
                // Precedence: per-app JSON first, then global AutoPackage.json (live,
                // then snapshot), then the built-in default.
                string perAppSubject = J("MailSubject");
                string subjectTpl = !string.IsNullOrWhiteSpace(perAppSubject) ? perAppSubject
                                  : !string.IsNullOrWhiteSpace(liveSubjectTpl) ? liveSubjectTpl
                                  : !string.IsNullOrWhiteSpace(_cfg?.MailSubject) ? _cfg!.MailSubject
                                  : "{PackageName} - Test";
                subjectTpl = subjectTpl.Trim();
                if (!HasToken(subjectTpl))
                    subjectTpl = subjectTpl + " {AppVendor} {AppName} {AppVersion}";
                string subject = string.Join(" ",
                    Expand(subjectTpl).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

                var body = new System.Text.StringBuilder();
                // Precedence: per-app JSON first, then global AutoPackage.json.
                string perAppBody = J("MailBody");
                string bodyTpl = !string.IsNullOrWhiteSpace(perAppBody) ? perAppBody
                              : !string.IsNullOrWhiteSpace(liveBodyTpl) ? liveBodyTpl
                              : (_cfg?.MailBody ?? "");
                if (!string.IsNullOrWhiteSpace(bodyTpl))
                {
                    // The user's text IS the message. Blank lines separate paragraphs;
                    // single line breaks become <br/>. Tokens are expanded, everything
                    // else is HTML-encoded plain text.
                    body.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#222;\">");
                    string expanded = Expand(bodyTpl).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
                    foreach (var para in expanded.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
                        body.Append("<p>").Append(E(para.Trim()).Replace("\n", "<br/>")).Append("</p>");
                    body.Append("</div>");
                }
                // Mail Body empty: the mail is sent with an empty body (subject and
                // attachments only) - by request, no built-in text is injected.

                // 6) Resolve mail settings from AutoPackage.json. _cfg is a snapshot from
                // page load (the same trap EnforceCopyLogsToDocuments hit): the Settings
                // window saves straight to the file, so read the live values at decision
                // time; the snapshot is only the fallback for an unreadable file.
                string mode   = string.IsNullOrWhiteSpace(_cfg?.MailMode) ? "Graph" : _cfg!.MailMode.Trim();
                string sender = _cfg?.MailSenderAddress ?? "";
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        using var liveDoc = JsonDocument.Parse(File.ReadAllText(ConfigPath),
                            new JsonDocumentOptions
                            {
                                CommentHandling     = JsonCommentHandling.Skip,
                                AllowTrailingCommas = true,
                            });
                        string LiveStr(string key) =>
                            liveDoc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                                ? (v.GetString() ?? "") : "";
                        string liveMode   = LiveStr("MailMode");
                        string liveSender = LiveStr("MailSenderAddress");
                        if (!string.IsNullOrWhiteSpace(liveMode))   mode   = liveMode.Trim();
                        if (!string.IsNullOrWhiteSpace(liveSender)) sender = liveSender.Trim();
                    }
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                if (string.IsNullOrWhiteSpace(sender))
                {
                    Log("Send mail: MailSenderAddress not set in AutoPackage.json - skipping.");
                    AutoFlowStatus.Set(AutoFlowStatus.SendMail, AutoFlowStatus.State.Failed);
                    return;
                }

                // 7) Locate the script (alongside the publish script).
                string scriptPath = IOPath.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Smekal", "SwiftPack Studio", "Templates", "AutoPackage_SendMail.ps1");
                if (!File.Exists(scriptPath))
                {
                    Log($"Send mail: script not found at {scriptPath} - skipping.");
                    AutoFlowStatus.Set(AutoFlowStatus.SendMail, AutoFlowStatus.State.Failed);
                    return;
                }

                // 8) Build arguments (write body to a temp file to avoid command-line length/escaping issues).
                string bodyFile = IOPath.Combine(IOPath.GetTempPath(), $"SwiftPackMail_{Guid.NewGuid():N}.html");
                File.WriteAllText(bodyFile, body.ToString(), System.Text.Encoding.UTF8);

                var sb = new System.Text.StringBuilder();
                string smtpPwdForEnv = "";   // SMTP password handed to the script via environment
                string graphSecretForEnv = "";   // Graph client secret handed to the script via environment
                sb.Append($"-ExecutionPolicy Bypass -NonInteractive -File \"{scriptPath}\"");
                sb.Append($" -Mode \"{mode}\"");
                sb.Append($" -Sender \"{sender}\"");
                sb.Append($" -Recipients \"{ownerEmail}\"");
                sb.Append($" -Subject \"{subject.Replace("\"", "'")}\"");
                sb.Append($" -BodyHtmlFile \"{bodyFile}\"");
                if (attachments.Count > 0)
                    sb.Append($" -Attachments \"{string.Join(";", attachments)}\"");

                if (mode.Equals("SMTP", StringComparison.OrdinalIgnoreCase))
                {
                    // SMTP credentials: AutoPackage.json first (SmtpUsername + DPAPI-encrypted
                    // password), then Credential Manager "SwiftPack_Mail" (UserName = SMTP login,
                    // Password = SMTP password). For providers like SMTP2GO the login username
                    // differs from the sender address — pass it explicitly via -SmtpUsername.
                    string smtpUser = _cfg?.SmtpUsername ?? "";
                    string pwd = SecretProtector.Decrypt(_cfg?.SmtpAppPasswordEncrypted ?? "");
                    if (string.IsNullOrWhiteSpace(pwd) || string.IsNullOrWhiteSpace(smtpUser))
                    {
                        var mc = MailCredentialManager.ReadCredential("SwiftPack_Mail");
                        if (mc != null)
                        {
                            if (string.IsNullOrWhiteSpace(smtpUser) && !string.IsNullOrWhiteSpace(mc.UserName))
                            {
                                smtpUser = mc.UserName;
                                Log("Send mail: SMTP username from Credential Manager (SwiftPack_Mail).");
                            }
                            if (string.IsNullOrWhiteSpace(pwd) && !string.IsNullOrWhiteSpace(mc.Password))
                            {
                                pwd = mc.Password;
                                Log("Send mail: SMTP password from Credential Manager (SwiftPack_Mail).");
                            }
                        }
                    }
                    if (string.IsNullOrWhiteSpace(pwd))
                    {
                        Log("Send mail: FAILED - no SMTP password found.");
                        Log("  Store it with: cmdkey /generic:SwiftPack_Mail /user:<smtp-username> /pass:<smtp-password>");
                        Log("  (or set SmtpAppPasswordEncrypted in AutoPackage.json).");
                        AutoFlowStatus.Set(AutoFlowStatus.SendMail, AutoFlowStatus.State.Failed);
                        try { File.Delete(bodyFile); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        return;
                    }
                    sb.Append($" -SmtpServer \"{_cfg?.SmtpServer ?? "smtp.office365.com"}\"");
                    sb.Append($" -SmtpPort {(_cfg?.SmtpPort > 0 ? _cfg!.SmtpPort : 587)}");
                    if (!string.IsNullOrWhiteSpace(smtpUser))
                        sb.Append($" -SmtpUsername \"{smtpUser}\"");
                    // The password is handed over via an environment variable instead of the
                    // command line: special characters ('"', '$', '`') survive intact and the
                    // secret never appears in the process command line.
                    smtpPwdForEnv = pwd;
                }
                else // Graph
                {
                    var (tenantId, clientId, clientSecret) = ResolveGraphCreds();
                    sb.Append($" -TenantId \"{tenantId}\"");
                    sb.Append($" -ClientId \"{clientId}\"");
                    // The secret is handed over via an environment variable instead of the
                    // command line (same as the SMTP password): special characters survive
                    // intact and the secret never appears in the process command line.
                    graphSecretForEnv = clientSecret ?? "";
                }

                // 9) Launch and wait.
                Log($"Send mail: launching ({mode}) to {ownerEmail} with {attachments.Count} attachment(s).");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = sb.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                if (!string.IsNullOrWhiteSpace(smtpPwdForEnv))
                    psi.EnvironmentVariables["SWIFTPACK_SMTP_PASSWORD"] = smtpPwdForEnv;
                if (!string.IsNullOrWhiteSpace(graphSecretForEnv))
                    psi.EnvironmentVariables["SWIFTPACK_CLIENT_SECRET"] = graphSecretForEnv;
                using var proc = System.Diagnostics.Process.Start(psi);
                string outp = proc?.StandardOutput.ReadToEnd() ?? "";
                string errp = proc?.StandardError.ReadToEnd() ?? "";
                proc?.WaitForExit(120000);
                int code = proc?.ExitCode ?? -1;

                foreach (var line in outp.Split('\n')) if (!string.IsNullOrWhiteSpace(line)) Log("  mail> " + line.TrimEnd());
                if (!string.IsNullOrWhiteSpace(errp)) Log("  mail!> " + errp.Trim());

                try { File.Delete(bodyFile); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                AutoFlowStatus.Set(AutoFlowStatus.SendMail,
                    code == 0 ? AutoFlowStatus.State.Ok : AutoFlowStatus.State.Failed);
                Log($"Send mail: {(code == 0 ? "OK" : "FAILED (exit " + code + ")")}");
            }
            catch (Exception ex)
            {
                Log("Send mail: ERROR - " + ex.Message);
                AutoFlowStatus.Set(AutoFlowStatus.SendMail, AutoFlowStatus.State.Failed);
            }
        }

        /// <summary>Resolve Graph creds the same way publishing does (AutoPackage.json + IntuneAuth.json).</summary>
        private (string tenantId, string clientId, string clientSecret) ResolveGraphCreds()
        {
            string tenantId = "", clientId = "", clientSecret = "";
            try
            {
                // TenantId/ClientId live in AutoPackage.json (IntuneSettings) - the same
                // source publishing uses. The auth JSON typically holds ONLY ClientSecret
                // (IntuneSecretID.json), so reading tenant/client exclusively from it left
                // Graph mail with empty -TenantId/-ClientId even when publishing worked.
                if (_cfg?.IntuneSettings != null)
                {
                    tenantId = _cfg.IntuneSettings.TenantId ?? "";
                    clientId = _cfg.IntuneSettings.ClientId ?? "";
                }
                string authPath = _cfg?.IntuneAuthPath ?? "";
                // Live re-read (same trap as the mail settings): the Settings window saves
                // straight to AutoPackage.json, so pick up values changed after page load.
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        using var liveDoc = JsonDocument.Parse(File.ReadAllText(ConfigPath),
                            new JsonDocumentOptions
                            {
                                CommentHandling     = JsonCommentHandling.Skip,
                                AllowTrailingCommas = true,
                            });
                        var liveRoot = liveDoc.RootElement;
                        if (liveRoot.TryGetProperty("IntuneAuthPath", out var ap) && ap.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(ap.GetString()))
                            authPath = ap.GetString()!;
                        if (liveRoot.TryGetProperty("IntuneSettings", out var isv) && isv.ValueKind == JsonValueKind.Object)
                        {
                            if (isv.TryGetProperty("TenantId", out var tv) && tv.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(tv.GetString())) tenantId = tv.GetString()!;
                            if (isv.TryGetProperty("ClientId", out var cv) && cv.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(cv.GetString())) clientId = cv.GetString()!;
                        }
                    }
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                if (!string.IsNullOrWhiteSpace(authPath) && File.Exists(authPath))
                {
                    using var d = System.Text.Json.JsonDocument.Parse(File.ReadAllText(authPath));
                    var r = d.RootElement;
                    string G(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? (v.GetString() ?? "") : "";
                    string tAuth = G("TenantId"), cAuth = G("ClientId");
                    if (!string.IsNullOrWhiteSpace(tAuth)) tenantId = tAuth;
                    if (!string.IsNullOrWhiteSpace(cAuth)) clientId = cAuth;
                    // Trim defensively: a stray space or newline pasted into the JSON value
                    // authenticates nowhere and is invisible in every log.
                    clientSecret = G("ClientSecret").Trim();
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            return (tenantId, clientId, clientSecret);
        }

        // ── Windows Credential Manager helper (read-only, generic credentials) ──
        // Used for "SwiftPack_Mail": UserName = SMTP login, Password = SMTP password.
        private static class MailCredentialManager
        {
            [System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "CredReadW",
                CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            private static extern bool CredRead(string target, uint type, uint flags,
                out IntPtr credential);

            [System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "CredFree")]
            private static extern void CredFree(IntPtr buffer);

            [System.Runtime.InteropServices.StructLayout(
                System.Runtime.InteropServices.LayoutKind.Sequential, CharSet =
                System.Runtime.InteropServices.CharSet.Unicode)]
            private struct CREDENTIAL
            {
                public uint     Flags;
                public uint     Type;
                public string   TargetName;
                public string   Comment;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
                public uint     CredentialBlobSize;
                public IntPtr   CredentialBlob;
                public uint     Persist;
                public uint     AttributeCount;
                public IntPtr   Attributes;
                public string   TargetAlias;
                public string   UserName;
            }

            public record Credential(string UserName, string Password);

            public static Credential? ReadCredential(string target)
            {
                try
                {
                    if (!CredRead(target, 1 /*CRED_TYPE_GENERIC*/, 0, out IntPtr ptr)) return null;
                    try
                    {
                        var cred = System.Runtime.InteropServices.Marshal.PtrToStructure<CREDENTIAL>(ptr);
                        string pwd = cred.CredentialBlobSize > 0
                            ? System.Runtime.InteropServices.Marshal.PtrToStringUni(
                                cred.CredentialBlob, (int)(cred.CredentialBlobSize / 2)) ?? ""
                            : "";
                        return new Credential(cred.UserName ?? "", pwd);
                    }
                    finally { CredFree(ptr); }
                }
                catch { return null; }
            }
        }

        // Intunewin / Publish tab auto-trigger — set in BtnCreate_Click, consumed in ShowSuccessDialog
        private bool   _triggerIntunewinTab = false;
        private bool   _triggerPublishTab   = false;
        private static bool _autoTriggerSave    = false; // set when PublishSettings=Yes  → clicks Save button
        private static bool _autoTriggerPublish = false; // set when PublishToIntune=Yes   → clicks Publish button after Intunewin
        private string _lastCreatedIntunewinPath = "";   // exact path from NotifyIntunewinCreated - used to pre-fill TxtIntunewinFile

        /// <summary>Stores the exact .intunewin path emitted by AutoIntunewinPage on success.</summary>
        internal void CaptureIntunewinPath(string path)
        {
            _lastCreatedIntunewinPath = path;
            // If Intunewin creation was the final configured step (no Publish follows),
            // the whole auto-flow is complete now.
            if (_intunewinIsTerminus)
            {
                _intunewinIsTerminus = false;
                RaiseAutoFlowComplete();
            }
        }
        private string _pendingAutoPublishAfterUninstall = ""; // "Yes" when PublishToIntune=Yes in per-app JSON
        private string _publishCapAppName   = "";
        private string _publishCapPublisher = "";
        private string _publishCapVersion   = "";
        private string _publishCapDesc      = "";
        // Chain that fires after Execute tab completes — passed as onAfterExecute to NavigateToExecuteTab
        private Action? _pendingAfterExecuteChain = null;
        public Action? ConsumePendingAfterExecuteChain()
        {
            var chain = _pendingAfterExecuteChain;
            _pendingAfterExecuteChain = null;
            return chain;
        }
        // Deferred Execute Uninstall — set by _onAfterCreateComplete (runs async via BeginInvoke).
        // Read lazily inside publishAction AFTER Intunewin completes, never in ShowSuccessDialog body.
        private Action? _pendingAfterPublishUninstallExecute = null;
        // Deferred "Uninstall source silently" command — set by SourceDownloadPage when
        // Intunewin/Publish is active. Holds the full silent uninstall command string so
        // publishAction can run it directly via Process (no Execute tab UI) after Publish.
        private static string _staticDeferredUninstallSourceCmd      = "";
        // Lazy variant: SourceDownloadPage sets this when the uninstall path isn't known yet
        // (app not yet installed at deferral time). publishAction builds the command from
        // ExeTxtUninstallSource + ExeTxtUninstallArgs which LookupInstalledByExe has by then.
        private static bool   _staticDeferredUninstallSourceLazy     = false;
        private static string _staticDeferredUninstallSourceLazyArgs = "";
        // True when publishAction has a deferred Execute Uninstall to fire — keeps GlobalShowMessageBox
        // false through IwBtnCreate_Click so ExecutePackagePage auto-closes its completion dialog.
        private bool _publishActionHasDeferredUninstall = false;
        private int    _msiLoadGen      = 0;           // incremented on every new MSI load; guards stale background callbacks
        private string _logPath         = "";          // %TEMP%\AutoPackage_*.log

        // ── Package-tab EVENT LOG window ────────────────────────────────────
        // Streams package-creation log lines into the cmd-style PkgEventLog TextBox
        // (defined in AutoPackagePage.xaml). Safe to call from any thread; marshals
        // to the UI thread and auto-scrolls to the newest line.
        /// <summary>Clears the Package-tab EVENT LOG window. Called by SourceDownloadPage
        /// when a new download starts so stale package-creation output doesn't linger.</summary>
        public void ClearPackageEventLog()
        {
            void Run()
            {
                try { if (PkgEventLog != null) PkgEventLog.Text = "Waiting for package creation…"; }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            if (Dispatcher.CheckAccess()) Run(); else Dispatcher.BeginInvoke((Action)Run);
        }

        /// <summary>
        /// Clear the Document tab and its cached state. Called when a new download starts so
        /// stale data from a previous package doesn't linger.
        /// </summary>
        // When true (set on a new download), the Document tab stays blank until a new package
        // run repopulates it — so stale data from a previous package doesn't get re-derived
        // from on-disk JSON/registry/logs when the tab is reopened.
        private bool _documentSuppressed = false;

        public void ClearDocumentTab()
        {
            _documentSuppressed  = true;
            _docPackageLocation  = "";
            _docSourceLocation   = "";
            _docInstallLogPath   = "";
            _docUninstallLogPath = "";
            _docIntuneJsonPath   = null;
            _docIntunewinPath    = null;
            _docRegDisplayName = ""; _docRegPublisher = ""; _docRegVersion = "";
            _docRegInstallLocation = ""; _docRegInstallSource = "";
            void Run() { try { _autoDocumentPage?.Clear(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } }
            if (Dispatcher.CheckAccess()) Run(); else Dispatcher.BeginInvoke((Action)Run);
        }

        private void AppendPkgEventLog(string line)
        {
            void Run()
            {
                try
                {
                    if (PkgEventLog == null) return;
                    if (PkgEventLog.Text == "Waiting for package creation…") PkgEventLog.Clear();
                    PkgEventLog.AppendText(line + Environment.NewLine);
                    PkgEventLog.CaretIndex = PkgEventLog.Text.Length;
                    PkgEventLogScroller?.ScrollToEnd();
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            if (Dispatcher.CheckAccess()) Run(); else Dispatcher.BeginInvoke((Action)Run);
        }
        private string _packageRootPath = "";          // Path1 = cfg.PackageRootPath\PackageName
        private bool   _suppressNameRebuild = false;   // prevent recursion while setting fields
        private bool   _busy = false;                  // Create is running

        /// <summary>When true the current Create was triggered from Source Download (Create Package checked).
        /// Folder-exists → auto-increment revision instead of showing dialog.</summary>
        private bool _triggeredFromSourceDownload = false;

        /// <summary>Full path of the most recently created package folder.</summary>
        public string LastCreatedPackageFolder { get; private set; } = "";
        private bool   _suppressVisibilityUpdate = false; // batch combo init
        private DateTime _createStartTime = DateTime.MinValue;              // set when Create begins

        // ── Cancel / Pause support ────────────────────────────────────
        private System.Threading.CancellationTokenSource? _createCts;
        private readonly System.Threading.ManualResetEventSlim _createPause = new(true); // set=running, reset=paused
        private System.Diagnostics.Process? _activeIntuneProc;  // currently running IntuneWinAppUtil process
        private volatile bool _creationPaused = false;          // true while cancel-confirm dialog is open

        // Win32: suspend / resume every thread in a process
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint SuspendThread(IntPtr hThread);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern int ResumeThread(IntPtr hThread);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
        private const uint THREAD_SUSPEND_RESUME = 0x0002;

        private void SuspendProcess(System.Diagnostics.Process proc)
        {
            try
            {
                foreach (ProcessThread thread in proc.Threads)
                {
                    IntPtr hThread = OpenThread(THREAD_SUSPEND_RESUME, false, (uint)thread.Id);
                    if (hThread != IntPtr.Zero) { SuspendThread(hThread); CloseHandle(hThread); }
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        private void ResumeProcess(System.Diagnostics.Process proc)
        {
            try
            {
                foreach (ProcessThread thread in proc.Threads)
                {
                    IntPtr hThread = OpenThread(THREAD_SUSPEND_RESUME, false, (uint)thread.Id);
                    if (hThread != IntPtr.Zero) { ResumeThread(hThread); CloseHandle(hThread); }
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        // ── Template-hosted controls (resolved after TabControl.ApplyTemplate) ────────

        private T TC<T>(string n) where T : FrameworkElement
        {
            MainTabControl.ApplyTemplate();
            return (T)MainTabControl.Template.FindName(n, MainTabControl)!;
        }
        // Toolbar controls are now direct XAML named elements — use FindName (not TC<>)
        private TextBlock    TxtStatus_Ctrl      => (TextBlock)   FindName("TxtStatus")!;
        private ProgressBar  PrgProgress_Ctrl    => (ProgressBar) FindName("PrgProgress")!;
        private Button       BtnCreate_Ctrl      => (Button)      FindName("BtnCreate")!;
        private Button       BtnClearAll_Ctrl    => (Button)      FindName("BtnClearAll")!;
        private Button       BtnRefresh_Ctrl     => (Button)      FindName("BtnRefresh")!;
        private Button       BtnCancel_Ctrl      => (Button)      FindName("BtnCancel")!;
        // BtnTabMsi / BtnTabExe removed — tab is now auto-switched by source extension

        // ── Constructor ───────────────────────────────────────────────────
        public AutoPackagePage()
        {
            InitializeComponent();
            Loaded += OnLoaded;

            // While the whole flow runs, disable the sub-tab bar so the user cannot switch
            // tabs manually; the automation still switches programmatically via ActivateTopTab.
            AutoFlowStatus.FlowRunningChanged -= OnFlowRunningChanged;
            AutoFlowStatus.FlowRunningChanged += OnFlowRunningChanged;
            OnFlowRunningChanged();
        }

        // Enable/disable the six sub-tab buttons based on the flow-running flag.
        private void OnFlowRunningChanged()
        {
            void Apply()
            {
                bool locked = AutoFlowStatus.FlowRunning;
                SetTabButtonEnabled(BtnTabDownload,  !locked);
                SetTabButtonEnabled(BtnTabPackage,   !locked);
                SetTabButtonEnabled(BtnTabExecute,   !locked);
                SetTabButtonEnabled(BtnTabIntunewin, !locked);
                SetTabButtonEnabled(BtnTabPublish,   !locked);
                SetTabButtonEnabled(BtnTabDocument,  !locked);
            }
            if (Dispatcher.CheckAccess()) Apply();
            else Dispatcher.Invoke(Apply);
        }

        private static void SetTabButtonEnabled(System.Windows.Controls.Button? b, bool enabled)
        {
            if (b == null) return;
            // Block/allow user clicks WITHOUT changing the tab's look: keep IsEnabled=true
            // (so no disabled greying) and leave Opacity alone (no dimming). IsHitTestVisible
            // stops mouse clicks; Focusable stops keyboard activation.
            b.IsHitTestVisible = enabled;
            b.Focusable        = enabled;
        }

        // ── Download / Package tab switching ──────────────────────────────
        private void BtnTabDownload_Click(object sender, RoutedEventArgs e) => ActivateTopTab(activeTab: "Download");
        private void BtnTabPackage_Click(object sender, RoutedEventArgs e)  => ActivateTopTab(activeTab: "Package");
        private void BtnTabExecute_Click(object sender, RoutedEventArgs e)  => ActivateTopTab(activeTab: "Execute");
        private void BtnTabIntunewin_Click(object sender, RoutedEventArgs e) => ActivateTopTab(activeTab: "Intunewin");
        private void BtnTabDocument_Click(object sender, RoutedEventArgs e) => ActivateTopTab(activeTab: "Document");
        private void BtnTabPublish_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(LastCreatedPackageFolder))
                NavigateToPublishTab(LastCreatedPackageFolder,
                    appName: _publishCapAppName, appVendor: _publishCapPublisher,
                    appVersion: _publishCapVersion, description: _publishCapDesc);
            else
                ActivateTopTab(activeTab: "Publish");
        }

        /// <summary>Called by SourceDownloadPage (hosted in FrameDownload) to switch to the Package tab
        /// when a download completes and Create Package is checked.</summary>
        public void SwitchToPackageTab() => Dispatcher.Invoke(() => ActivateTopTab(activeTab: "Package"));

        /// <summary>
        /// Routes silent-phase progress to the SourceDownloadPage actually hosted inside
        /// FrameDownload (the visible instance), not whatever instance the host window
        /// happens to hold. Called by the host (MainWindow) during silent
        /// Install/Uninstall source so the thin progress bar + label highlight appear on
        /// the page the user is looking at. Reflection keeps AutoPackagePage decoupled
        /// from SourceDownloadPage at compile time.
        /// </summary>
        public void ForwardSilentPhaseToDownload(string method, string? phase = null)
        {
            void Run()
            {
                try
                {
                    var content = FrameDownload?.Content;
                    if (content == null) return;
                    var mi = content.GetType().GetMethod(method,
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public);
                    if (mi == null) return;
                    var pars = mi.GetParameters();
                    mi.Invoke(content, pars.Length == 1 ? new object?[] { phase ?? "" } : System.Array.Empty<object?>());
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            if (Dispatcher.CheckAccess()) Run(); else Dispatcher.Invoke(Run);
        }

        // ── Execute tab: AutoExecutePage — self-contained, no dependency on ExecutePackagePage ──
        private AutoExecutePage? _autoExecPage = null;

        private void EnsureAutoExecPageLoaded()
        {
            if (_autoExecPage != null) return;
            _autoExecPage = new AutoExecutePage();
            _autoExecPage.SetContext(new AutoIntunewinContext(this));
            FrameExecute?.Navigate(_autoExecPage);
        }

        // ── Publish tab: AutoPublishPage — self-contained, no dependency on PublishDeployPage ──
        private AutoPublishPage? _autoPublishPage = null;

        // ── Document tab ──────────────────────────────────────────────────
        private AutoDocumentPage? _autoDocumentPage = null;

        private void EnsureAutoDocumentPageLoaded()
        {
            if (_autoDocumentPage != null) return;
            _autoDocumentPage = new AutoDocumentPage();
            FrameDocument?.Navigate(_autoDocumentPage);
        }

        /// <summary>Rebuild the document model from current state and show it on the Document tab.</summary>
        public void RefreshDocumentTab()
        {
            try
            {
                EnsureAutoDocumentPageLoaded();
                // While suppressed (after a new download, before a new package run), keep the
                // tab blank instead of re-deriving stale data from on-disk JSON/registry/logs.
                if (_documentSuppressed)
                {
                    _autoDocumentPage?.Clear();
                    return;
                }
                var model = BuildDocumentModel();
                _autoDocumentPage?.Populate(model);
            }
            catch { /* display-only; never crash */ }
        }

        /// <summary>
        /// Assemble the document model from the per-app values (sel), the resolved Intune
        /// JSON (if present next to the .intunewin) and the two %TEMP% AutoPackage logs.
        /// </summary>
        private AutoDocumentPage.DocumentModel BuildDocumentModel()
        {
            var m   = new AutoDocumentPage.DocumentModel();
            var sel = _staticSelectedApp;

            string pkgName = TxtPackageName?.Text?.Trim() ?? "";

            // ── Most reliable source: the Publish tab's Intunewin file path. The Intune JSON
            //    sits right beside it (same name, .json), and the package root is two levels
            //    up (……\{PackageName}\Intune\{PackageName}.intunewin). This works even when the
            //    Document tab is opened standalone (no create run this session).
            string iwPath = "";
            try { iwPath = _autoPublishPage?.IntunewinFilePath ?? ""; } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            if (string.IsNullOrWhiteSpace(iwPath)) iwPath = _docIntunewinPath ?? "";

            string jsonPath = "";
            string pkgRoot  = _docPackageLocation;

            if (!string.IsNullOrWhiteSpace(iwPath))
            {
                try
                {
                    string cand = IOPath.ChangeExtension(iwPath, ".json");
                    if (File.Exists(cand)) jsonPath = cand;
                    // …\{PackageName}\Intune\{PackageName}.intunewin  → package root = parent of "Intune"
                    string? intuneDir = IOPath.GetDirectoryName(iwPath);
                    string? root      = intuneDir != null ? IOPath.GetDirectoryName(intuneDir) : null;
                    if (!string.IsNullOrWhiteSpace(root)) pkgRoot = root;
                    if (string.IsNullOrWhiteSpace(pkgName))
                        pkgName = IOPath.GetFileNameWithoutExtension(iwPath);
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }

            m.PackageName = pkgName;

            // ── Resolve the package root from other sources if still unknown ──
            if (string.IsNullOrWhiteSpace(pkgRoot)) pkgRoot = _packageRootPath;
            if (string.IsNullOrWhiteSpace(pkgRoot) && !string.IsNullOrWhiteSpace(pkgName))
            {
                try
                {
                    string outRoot = _cfg?.ResolvedPackageOutputFolder ?? "";
                    if (!string.IsNullOrWhiteSpace(outRoot))
                    {
                        string cand = IOPath.Combine(outRoot, pkgName);
                        if (Directory.Exists(cand)) pkgRoot = cand;
                    }
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            m.PackageLocation = pkgRoot;

            // ── Fall back to other JSON locations if the intunewin-adjacent one wasn't found ──
            if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
            {
                if (!string.IsNullOrWhiteSpace(_docIntuneJsonPath) && File.Exists(_docIntuneJsonPath))
                    jsonPath = _docIntuneJsonPath!;
            }
            if ((string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath)) &&
                !string.IsNullOrWhiteSpace(pkgRoot) && !string.IsNullOrWhiteSpace(pkgName))
            {
                string cand = IOPath.Combine(pkgRoot, "Intune", pkgName + ".json");
                if (File.Exists(cand)) jsonPath = cand;
            }

            // ── Package Details from the UNINSTALL REGISTRY HIVE ──
            // Prefer values captured DURING the install (the app is uninstalled later in the
            // flow, so a live read at document time would find nothing). Fall back to a live
            // read if nothing was captured (e.g. opening the tab while the app is installed).
            (string DisplayName, string Publisher, string DisplayVersion, string InstallLocation, string InstallSource) reg;
            if (!string.IsNullOrWhiteSpace(_docRegDisplayName) || !string.IsNullOrWhiteSpace(_docRegInstallLocation))
                reg = (_docRegDisplayName, _docRegPublisher, _docRegVersion, _docRegInstallLocation, _docRegInstallSource);
            else
                reg = ReadUninstallRegistryForApp(sel, sel?.DetectionMsiProductCode ?? "");
            m.DisplayName     = reg.DisplayName;
            m.Publisher       = reg.Publisher;
            m.Version         = reg.DisplayVersion;
            m.InstallLocation = reg.InstallLocation;
            // Fallbacks when the installer wrote no InstallLocation in the registry:
            // the detection File rule's folder (captured while installed), then the
            // folder of the app's uninstall executable.
            if (string.IsNullOrWhiteSpace(m.InstallLocation))
                m.InstallLocation = sel?.DetectionFileFolderPath?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(m.InstallLocation))
            {
                try
                {
                    string up = sel?.UninstallPath?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(up))
                        m.InstallLocation = IOPath.GetDirectoryName(up) ?? "";
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }

            // ── Source Location — from the no-revision install log, else registry InstallSource ──
            m.SourceLocation = _docSourceLocation;
            if (string.IsNullOrWhiteSpace(m.SourceLocation))
                m.SourceLocation = ReadSourceLocationFromLog(sel, pkgName);
            if (string.IsNullOrWhiteSpace(m.SourceLocation))
                m.SourceLocation = reg.InstallSource;

            // ── Install / Uninstall log paths — the PSADT logs in the package Documents folder ──
            m.InstallLogPath   = _docInstallLogPath;
            m.UninstallLogPath = _docUninstallLogPath;
            if ((string.IsNullOrWhiteSpace(m.InstallLogPath) || string.IsNullOrWhiteSpace(m.UninstallLogPath))
                && !string.IsNullOrWhiteSpace(pkgRoot))
            {
                try
                {
                    string docsDir = IOPath.Combine(ResolveOutputBase(pkgRoot), "Documents");
                    if (Directory.Exists(docsDir))
                    {
                        if (string.IsNullOrWhiteSpace(m.InstallLogPath))
                            m.InstallLogPath = Directory.GetFiles(docsDir, "*Install.log")
                                .FirstOrDefault(f => !f.EndsWith("Uninstall.log", StringComparison.OrdinalIgnoreCase)) ?? "";
                        if (string.IsNullOrWhiteSpace(m.UninstallLogPath))
                            m.UninstallLogPath = Directory.GetFiles(docsDir, "*Uninstall.log").FirstOrDefault() ?? "";
                    }
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }

            // ── Intune Details (from the resolved Intune JSON) ──
            try
            {
                if (!string.IsNullOrWhiteSpace(jsonPath) && File.Exists(jsonPath))
                {
                    _docIntuneJsonPath = jsonPath;
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
                    var r = doc.RootElement;
                    string G(string k) => r.TryGetProperty(k, out var v) ? (v.GetString() ?? "") : "";

                    m.IntuneName        = G("IntuneAppName");
                    m.IntunePublisher   = G("IntuneAppVendor");
                    m.IntuneDescription = G("Description");
                    m.IntuneCategory    = G("IntuneCategory");
                    m.InstallCommand    = G("InstallCommand");
                    m.UninstallCommand  = G("UninstallCommand");
                    m.InstallBehavior   = G("InstallBehavior");
                    m.AppOwnerEmail     = !string.IsNullOrWhiteSpace(G("AppOwnerEmail")) ? G("AppOwnerEmail") : G("Owner");
                    m.UatUsers          = G("UATUsers");

                    var det = new List<string>();

                    if (G("DetectionTypeMSI") == "Yes")
                    {
                        var ln = new List<string>
                        {
                            "MSI",
                            $"  MSI product code   {G("DetectionMsiProductCode")}",
                            $"  Version check      {(G("DetectionMsiVersionCheck") == "Yes" ? "Yes" : "No")}",
                        };
                        if (G("DetectionMsiVersionCheck") == "Yes")
                        {
                            if (!string.IsNullOrWhiteSpace(G("DetectionMsiOperator"))) ln.Add($"  Operator           {G("DetectionMsiOperator")}");
                            if (!string.IsNullOrWhiteSpace(G("DetectionMsiValue")))    ln.Add($"  Value              {G("DetectionMsiValue")}");
                        }
                        det.Add(string.Join(Environment.NewLine, ln));
                    }

                    if (G("DetectionTypeFile") == "Yes")
                    {
                        string method = G("DetectionFileDetectionMethod");
                        // Pick the value field that matches the detection method.
                        string fileVal;
                        if (method.IndexOf("Size", StringComparison.OrdinalIgnoreCase) >= 0)
                            fileVal = G("DetectionFileSizeValue");
                        else if (method.IndexOf("Date", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Date methods store date + time separately — combine them.
                            string d = G("DetectionFileDateValue");
                            string t = G("DetectionFileTimeValue");
                            fileVal = string.IsNullOrWhiteSpace(t) ? d : $"{d} {t}".Trim();
                        }
                        else if (method.IndexOf("Version", StringComparison.OrdinalIgnoreCase) >= 0)
                            fileVal = G("DetectionFileValue");
                        else
                            fileVal = !string.IsNullOrWhiteSpace(G("DetectionFileValue")) ? G("DetectionFileValue") : G("DetectionFileSizeValue");
                        var ln = new List<string>
                        {
                            "File",
                            $"  Path               {G("DetectionFileFolderPath")}",
                            $"  File or folder     {G("DetectionFileFileName")}",
                            $"  Detection method   {method}",
                        };
                        if (!string.IsNullOrWhiteSpace(G("DetectionFileOperator"))) ln.Add($"  Operator           {G("DetectionFileOperator")}");
                        if (!string.IsNullOrWhiteSpace(fileVal))                    ln.Add($"  Value              {fileVal}");
                        ln.Add($"  32-bit on 64-bit   {(G("DetectionFile32Bit") == "Yes" ? "Yes" : "No")}");
                        det.Add(string.Join(Environment.NewLine, ln));
                    }

                    if (G("DetectionTypeRegistry") == "Yes")
                    {
                        var ln = new List<string>
                        {
                            "Registry",
                            $"  Key path           {G("DetectionRegKeyPath")}",
                        };
                        if (!string.IsNullOrWhiteSpace(G("DetectionRegValueName"))) ln.Add($"  Value name         {G("DetectionRegValueName")}");
                        ln.Add($"  Detection method   {G("DetectionRegDetectionMethod")}");
                        if (!string.IsNullOrWhiteSpace(G("DetectionRegOperator")))   ln.Add($"  Operator           {G("DetectionRegOperator")}");
                        if (!string.IsNullOrWhiteSpace(G("DetectionRegistryValue"))) ln.Add($"  Value              {G("DetectionRegistryValue")}");
                        ln.Add($"  32-bit on 64-bit   {(G("DetectionReg32Bit") == "Yes" ? "Yes" : "No")}");
                        det.Add(string.Join(Environment.NewLine, ln));
                    }

                    if (!string.IsNullOrWhiteSpace(G("DetectionScriptFile")))
                    {
                        var ln = new List<string>
                        {
                            "Script",
                            $"  Script file        {G("DetectionScriptFile")}",
                            $"  Enforce signature  {(G("DetectionScriptEnforceSignatureCheck") == "Yes" ? "Yes" : "No")}",
                            $"  Run as 32-bit      {(G("DetectionScriptRunAs32Bit") == "Yes" ? "Yes" : "No")}",
                        };
                        det.Add(string.Join(Environment.NewLine, ln));
                    }

                    // Blank line between rule types.
                    m.DetectionRule = string.Join(Environment.NewLine + Environment.NewLine, det);

                    var deps = (sel?.Dependencies ?? new List<DependencyItem>())
                               .Where(d => !string.IsNullOrWhiteSpace(d.AppName) || !string.IsNullOrWhiteSpace(d.AppID))
                               .Select(d => $"{(string.IsNullOrWhiteSpace(d.AppName) ? "(unnamed)" : d.AppName)} (Auto Install: {(string.IsNullOrWhiteSpace(d.AutoInstall) ? "No" : d.AutoInstall)}) [{d.AppID}]");
                    m.Dependencies = string.Join(Environment.NewLine, deps);
                    var sups = (sel?.Supersedences ?? new List<SupersedenceItem>())
                               .Where(s => !string.IsNullOrWhiteSpace(s.AppName) || !string.IsNullOrWhiteSpace(s.AppID))
                               .Select(s => $"{(string.IsNullOrWhiteSpace(s.AppName) ? "(unnamed)" : s.AppName)} (Uninstall Prev.: {(string.IsNullOrWhiteSpace(s.UninstallPreviousVersion) ? "No" : s.UninstallPreviousVersion)}) [{s.AppID}]");
                    m.Supersedence = string.Join(Environment.NewLine, sups);

                    var asg = new List<string>();
                    if (!string.IsNullOrWhiteSpace(G("AssignmentsRequired")))  asg.Add($"Required: {G("AssignmentsRequired")}");
                    if (!string.IsNullOrWhiteSpace(G("AssignmentsAvailable"))) asg.Add($"Available: {G("AssignmentsAvailable")}");
                    if (!string.IsNullOrWhiteSpace(G("AssignmentsUninstall"))) asg.Add($"Uninstall: {G("AssignmentsUninstall")}");
                    m.Assignments = string.Join("; ", asg);
                }
            }
            catch { /* JSON missing/partial - leave Intune fields blank */ }

            // ── Fallbacks for Package Details when registry didn't resolve ──
            if (string.IsNullOrWhiteSpace(m.DisplayName)) m.DisplayName = !string.IsNullOrWhiteSpace(m.IntuneName) ? m.IntuneName : (sel?.SourceAppName ?? "");
            if (string.IsNullOrWhiteSpace(m.Publisher))   m.Publisher   = !string.IsNullOrWhiteSpace(m.IntunePublisher) ? m.IntunePublisher : (sel?.SourceAppVendor ?? "");
            if (string.IsNullOrWhiteSpace(m.Version))     m.Version     = sel?.SourceAppVersion ?? "";

            // Intune identity fallbacks.
            if (string.IsNullOrWhiteSpace(m.IntuneName))      m.IntuneName      = sel?.IntuneAppName ?? "";
            if (string.IsNullOrWhiteSpace(m.IntunePublisher)) m.IntunePublisher = sel?.IntuneAppVendor ?? "";
            if (string.IsNullOrWhiteSpace(m.UatUsers))        m.UatUsers        = sel?.UATUsers ?? "";
            if (string.IsNullOrWhiteSpace(m.AppOwnerEmail))   m.AppOwnerEmail   =
                !string.IsNullOrWhiteSpace(sel?.AppOwnerEmail) ? sel!.AppOwnerEmail : (sel?.Owner ?? "");

            return m;
        }

        // Reads the Windows uninstall registry for an app's DisplayName/Publisher/
        // DisplayVersion/InstallLocation/InstallSource. Matches by ProductCode subkey (MSI
        // GUID) first, else by DisplayName containing the source app name. Checks 64/32-bit hives.
        private (string DisplayName, string Publisher, string DisplayVersion, string InstallLocation, string InstallSource)
            ReadUninstallRegistry(string appNameContains, string productCode)
        {
            string dn = "", pub = "", ver = "", loc = "", src = "";
            // Many EXE installers (NSIS/Inno) write no InstallLocation value.
            // Derive it from UninstallString's folder when missing.
            static string LocFromUninstallString(string uninst)
            {
                try
                {
                    uninst = (uninst ?? "").Trim();
                    if (uninst.Length == 0) return "";
                    string exe = uninst.StartsWith("\"")
                        ? uninst.Substring(1, Math.Max(0, uninst.IndexOf('"', 1) - 1))
                        : uninst.Split(' ')[0];
                    return System.IO.Path.GetDirectoryName(exe) ?? "";
                }
                catch { return ""; }
            }
            try
            {
                string[] paths =
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };
                // Check machine-wide installs first, then per-user installs (HKCU) —
                // many EXE installers register only under the current user's hive.
                var hives = new[] { Microsoft.Win32.Registry.LocalMachine,
                                    Microsoft.Win32.Registry.CurrentUser };
                foreach (var hive in hives)
                foreach (var rootPath in paths)
                {
                    using var root = hive.OpenSubKey(rootPath);
                    if (root == null) continue;

                    // 1) Direct ProductCode subkey (e.g. {GUID}) for MSI apps.
                    if (!string.IsNullOrWhiteSpace(productCode))
                    {
                        using var pc = root.OpenSubKey(productCode);
                        if (pc != null)
                        {
                            dn  = pc.GetValue("DisplayName")     as string ?? "";
                            pub = pc.GetValue("Publisher")       as string ?? "";
                            ver = pc.GetValue("DisplayVersion")  as string ?? "";
                            loc = pc.GetValue("InstallLocation") as string ?? "";
                            if (string.IsNullOrWhiteSpace(loc))
                                loc = LocFromUninstallString(pc.GetValue("UninstallString") as string ?? "");
                            src = pc.GetValue("InstallSource")   as string ?? "";
                            if (!string.IsNullOrWhiteSpace(dn)) return (dn, pub, ver, loc, src);
                        }
                    }

                    // 2) Scan for a DisplayName containing the app name.
                    if (!string.IsNullOrWhiteSpace(appNameContains))
                    {
                        foreach (var sub in root.GetSubKeyNames())
                        {
                            using var k = root.OpenSubKey(sub);
                            string d = k?.GetValue("DisplayName") as string ?? "";
                            if (d.IndexOf(appNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                dn  = d;
                                pub = k?.GetValue("Publisher")       as string ?? "";
                                ver = k?.GetValue("DisplayVersion")  as string ?? "";
                                loc = k?.GetValue("InstallLocation") as string ?? "";
                                if (string.IsNullOrWhiteSpace(loc))
                                    loc = LocFromUninstallString(k?.GetValue("UninstallString") as string ?? "");
                                src = k?.GetValue("InstallSource")   as string ?? "";
                                return (dn, pub, ver, loc, src);
                            }
                        }
                    }
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            return (dn, pub, ver, loc, src);
        }

        /// <summary>
        /// Registry lookup trying every plausible name for the app: ProductCode +
        /// SourceAppName first, then IntuneAppName. Returns the first hit with a
        /// DisplayName so per-app naming differences don't lose the Document
        /// tab's Version / Install Location values.
        /// </summary>
        private (string DisplayName, string Publisher, string DisplayVersion, string InstallLocation, string InstallSource)
            ReadUninstallRegistryForApp(AutoPackageAppEntry? sel, string productCode)
        {
            var r = ReadUninstallRegistry(sel?.SourceAppName ?? "", productCode);
            if (!string.IsNullOrWhiteSpace(r.DisplayName)) return r;
            string intuneName = sel?.IntuneAppName ?? "";
            if (!string.IsNullOrWhiteSpace(intuneName) &&
                !intuneName.Equals(sel?.SourceAppName ?? "", StringComparison.OrdinalIgnoreCase))
            {
                var r2 = ReadUninstallRegistry(intuneName, "");
                if (!string.IsNullOrWhiteSpace(r2.DisplayName)) return r2;
            }
            return r;
        }

        // Registry values captured WHILE the app is installed (before the source uninstall),
        // so the Document tab/generated docs have them even though the app is later removed.
        private string _docRegDisplayName     = "";
        private string _docRegPublisher       = "";
        private string _docRegVersion         = "";
        private string _docRegInstallLocation = "";
        private string _docRegInstallSource   = "";

        /// <summary>
        /// Capture the app's uninstall-registry details NOW (called right after the source
        /// is installed, before it gets uninstalled). Stores them for the document.
        /// </summary>
        public void CaptureUninstallRegistryForDocument()
        {
            try
            {
                var sel = _staticSelectedApp;
                var reg = ReadUninstallRegistryForApp(sel, sel?.DetectionMsiProductCode ?? "");
                if (!string.IsNullOrWhiteSpace(reg.DisplayName) || !string.IsNullOrWhiteSpace(reg.InstallLocation))
                {
                    _docRegDisplayName     = reg.DisplayName;
                    _docRegPublisher       = reg.Publisher;
                    _docRegVersion         = reg.DisplayVersion;
                    _docRegInstallLocation = reg.InstallLocation;
                    _docRegInstallSource   = reg.InstallSource;
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        // Reads "Folder:" / "File:" from the no-revision install log
        // (AutoPackage_{vendor}_{app}_{version}.log) to recover the original source location.
        private string ReadSourceLocationFromLog(AutoPackageAppEntry? sel, string pkgName = "")
        {
            try
            {
                string tmp = IOPath.GetTempPath();
                var candidates = new List<string>();

                string san(string t) => new string((t ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
                string vendor = san(sel?.SourceAppVendor ?? sel?.IntuneAppVendor ?? "");
                string app    = san(sel?.SourceAppName   ?? sel?.IntuneAppName   ?? "");
                string ver    = (sel?.SourceAppVersion ?? sel?.IntuneAppVersion ?? "").Trim();

                // 1) Reconstruct from vendor/app/version (sanitised — no whitespace).
                if (!string.IsNullOrWhiteSpace(vendor) && !string.IsNullOrWhiteSpace(app))
                    candidates.Add(IOPath.Combine(tmp, $"AutoPackage_{vendor}_{app}_{ver}.log"));

                // 2) Derive from the package name by stripping the trailing revision (…_NN).
                if (string.IsNullOrWhiteSpace(pkgName))
                {
                    try { pkgName = TxtPackageName?.Text?.Trim() ?? ""; } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
                if (!string.IsNullOrWhiteSpace(pkgName))
                {
                    string noRev = System.Text.RegularExpressions.Regex.Replace(pkgName, @"_\d+$", "");
                    candidates.Add(IOPath.Combine(tmp, $"AutoPackage_{noRev}.log"));
                }

                // Helper: read the source location from a log file. The MSI flow logs a
                // "Folder:" line; the EXE flow logs "EXE src …:" / "SourcePath :" file
                // paths — for those, the containing folder is the source location.
                string ReadSrcLoc(string path)
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
                    string[] markers = { "Folder:", "SourcePath", "EXE src (ExeTxtSource)", "MSI src" };
                    foreach (var line in File.ReadLines(path))
                    {
                        foreach (var mk in markers)
                        {
                            int idx = line.IndexOf(mk, StringComparison.OrdinalIgnoreCase);
                            if (idx < 0) continue;
                            int colon = line.IndexOf(':', idx + mk.Length - (mk.EndsWith(":") ? 1 : 0));
                            if (colon < 0) continue;
                            string val = line.Substring(colon + 1).Trim();
                            if (string.IsNullOrWhiteSpace(val)) continue;
                            // File path → use its folder; folder path → as-is.
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(IOPath.GetExtension(val)))
                                    val = IOPath.GetDirectoryName(val) ?? val;
                            }
                            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                            if (!string.IsNullOrWhiteSpace(val)) return val;
                        }
                    }
                    return "";
                }

                foreach (var path in candidates)
                {
                    string v = ReadSrcLoc(path);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }

                // 3) Fallback: scan %TEMP% for an AutoPackage_*.log whose name prefix-matches
                //    either the package's no-revision base OR the vendor/app/version base —
                //    covers logs written WITH the revision suffix and naming differences.
                try
                {
                    var baseNames = new List<string>();
                    if (!string.IsNullOrWhiteSpace(pkgName))
                        baseNames.Add("AutoPackage_" + System.Text.RegularExpressions.Regex.Replace(pkgName, @"_\d+$", ""));
                    if (!string.IsNullOrWhiteSpace(vendor) && !string.IsNullOrWhiteSpace(app))
                        baseNames.Add($"AutoPackage_{vendor}_{app}");

                    if (baseNames.Count > 0)
                    {
                        var files = Directory.GetFiles(tmp, "AutoPackage_*.log")
                            .Where(f => baseNames.Any(b => IOPath.GetFileNameWithoutExtension(f)
                                .StartsWith(b, StringComparison.OrdinalIgnoreCase)))
                            .OrderByDescending(f => File.GetLastWriteTimeUtc(f));
                        foreach (var f in files)
                        {
                            string v = ReadSrcLoc(f);
                            if (!string.IsNullOrWhiteSpace(v)) return v;
                        }
                    }
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            return "";
        }

        // Captured during the package flow so the Document tab + generated docs can use them.
        private string  _docSourceLocation   = "";
        private string  _docPackageLocation  = "";
        private string  _docShortcutPath     = "";
        private string  _docInstallLocation  = "";
        private string  _docInstallLogPath   = "";
        private string  _docUninstallLogPath = "";
        private string? _docIntuneJsonPath   = null;
        private string? _docIntunewinPath    = null;

        /// <summary>
        /// Finds the REAL deployment Invoke-AppDeployToolkit.ps1 under <paramref name="root"/>,
        /// ignoring the decoy copy bundled inside the PSAppDeployToolkit module
        /// (…\PSAppDeployToolkit\Frontend\v4\Invoke-AppDeployToolkit.ps1). Enumeration order is
        /// not contractual, so the choice is made explicitly: prefer a path NOT inside a
        /// \PSAppDeployToolkit\ module folder, then the shallowest path. Returns null if none.
        /// </summary>
        private static string? FindDeploymentPs1(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            try
            {
                var all = Directory.GetFiles(root, "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories);
                if (all.Length == 0) return null;
                return all
                    .OrderBy(p => p.IndexOf(@"\PSAppDeployToolkit\", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                    .ThenBy(p => p.Count(c => c == '\\' || c == '/'))
                    .ThenBy(p => p.Length)
                    .FirstOrDefault();
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            return null;
        }

        /// <summary>
        /// Resolves the "output base" folder — the parent of the directory that contains
        /// Invoke-AppDeployToolkit.ps1 — so that the Intune and Documents folders are created
        /// as SIBLINGS of the app-package folder, mirroring the PSADT template's own layout:
        ///   • ps1 at &lt;root&gt;\Package          → base = &lt;root&gt;          (template ps1 was at its root)
        ///   • ps1 at &lt;root&gt;\AppPackage       → base = &lt;root&gt;
        ///   • ps1 at &lt;root&gt;\Output\AppPackage → base = &lt;root&gt;\Output
        /// Falls back to the package root when no ps1 is present (e.g. no PSADT template).
        /// Idempotent given the layouts above, so it is safe to apply at every derivation site.
        /// </summary>
        private static string ResolveOutputBase(string packageRoot)
        {
            if (string.IsNullOrWhiteSpace(packageRoot)) return packageRoot ?? "";
            try
            {
                string? ps1 = FindDeploymentPs1(packageRoot);
                if (ps1 != null)
                {
                    string ps1Dir  = IOPath.GetDirectoryName(ps1) ?? packageRoot;
                    string? parent = IOPath.GetDirectoryName(ps1Dir);
                    if (!string.IsNullOrWhiteSpace(parent)) return parent!;
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            return packageRoot;
        }

        /// <summary>Case-insensitive, separator-tolerant full-path comparison.</summary>
        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(
                    IOPath.GetFullPath(a).TrimEnd('\\', '/'),
                    IOPath.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a?.TrimEnd('\\', '/'), b?.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// If CopytxtToDocuments / CopydocxToDocuments is "Yes" for this app, build the
        /// document model and write the .txt / .docx into the package's Documents folder.
        /// Safe to call multiple times; never throws.
        /// </summary>
        public void GeneratePackageDocuments(Action<string>? log = null)
        {
            try
            {
                var sel = _staticSelectedApp;
                bool wantTxt  = string.Equals(_cfg?.CopytxtToDocuments,  "Yes", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(sel?.CopytxtToDocuments,  "Yes", StringComparison.OrdinalIgnoreCase);
                bool wantDocx = string.Equals(_cfg?.CopydocxToDocuments, "Yes", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(sel?.CopydocxToDocuments, "Yes", StringComparison.OrdinalIgnoreCase);
                if (!wantTxt && !wantDocx) return;

                // Make sure the captured locations are current.
                if (string.IsNullOrWhiteSpace(_docPackageLocation) && !string.IsNullOrWhiteSpace(_packageRootPath))
                    _docPackageLocation = _packageRootPath;

                // BuildDocumentModel reads UI elements (TxtPackageName) — build it on the UI thread.
                AutoDocumentPage.DocumentModel model;
                if (Dispatcher.CheckAccess()) model = BuildDocumentModel();
                else model = Dispatcher.Invoke(() => BuildDocumentModel());

                string root = !string.IsNullOrWhiteSpace(_docPackageLocation) ? _docPackageLocation : _packageRootPath;
                if (string.IsNullOrWhiteSpace(root)) { log?.Invoke("[Document] No package location - skipping document generation."); return; }
                // Documents sit beside the app-package folder (sibling of the ps1's folder).
                string documentsFolder = IOPath.Combine(ResolveOutputBase(root), "Documents");

                var files = AutoDocumentPage.GenerateDocuments(model, documentsFolder, wantTxt, wantDocx, log);

                // Refresh the on-screen Document tab too.
                Dispatcher.Invoke(() => { try { _autoDocumentPage?.Populate(model); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } });

                foreach (var f in files) log?.Invoke($"[Document] Copied to Documents: {f}");
            }
            catch (Exception ex) { try { log?.Invoke($"[Document] generation error: {ex.Message}"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } }
        }

        /// <summary>
        /// Enforces the CopyLogsToDocuments flag from AutoPackage.json. PSAppDeployToolkit
        /// writes its Install/Uninstall .log files into the package Documents folder on
        /// every run; when CopyLogsToDocuments is not "Yes", those .log files are removed
        /// so logs only remain when explicitly requested. Safe to call repeatedly; never throws.
        /// </summary>
        private void EnforceCopyLogsToDocuments(Action<string>? log = null)
        {
            try
            {
                // The Settings window saves straight to AutoPackage.json, but _cfg is a
                // snapshot from page load - enforcing off the snapshot is exactly how logs
                // got deleted while the CURRENT setting said Yes. Read the live value at
                // decision time; the snapshot is only the fallback for an unreadable file.
                string flag = _cfg?.CopyLogsToDocuments ?? "No";
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        using var liveDoc = JsonDocument.Parse(File.ReadAllText(ConfigPath),
                            new JsonDocumentOptions
                            {
                                CommentHandling    = JsonCommentHandling.Skip,
                                AllowTrailingCommas = true,
                            });
                        if (liveDoc.RootElement.TryGetProperty("CopyLogsToDocuments", out var liveVal)
                            && liveVal.ValueKind == JsonValueKind.String)
                            flag = liveVal.GetString() ?? flag;
                    }
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                if (string.Equals(flag, "Yes", StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke("[Document] Logs kept in Documents (CopyLogsToDocuments=Yes)");
                    return;
                }

                if (string.IsNullOrWhiteSpace(_docPackageLocation) && !string.IsNullOrWhiteSpace(_packageRootPath))
                    _docPackageLocation = _packageRootPath;
                string root = !string.IsNullOrWhiteSpace(_docPackageLocation) ? _docPackageLocation : _packageRootPath;
                if (string.IsNullOrWhiteSpace(root)) return;

                string documentsFolder = IOPath.Combine(ResolveOutputBase(root), "Documents");
                if (!Directory.Exists(documentsFolder)) return;

                foreach (var lf in Directory.GetFiles(documentsFolder, "*.log"))
                {
                    try { File.Delete(lf); log?.Invoke($"[Document] Removed log (CopyLogsToDocuments=No): {IOPath.GetFileName(lf)}"); }
                    catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }


        private void EnsureAutoPublishPageLoaded()
        {
            if (_autoPublishPage != null) return;
            _autoPublishPage = new AutoPublishPage();
            _autoPublishPage.SetContext(new AutoIntunewinContext(this));
            // When PublishToIntune=Yes, the publish-to-Intune script is the LAST step
            // of the auto-flow. Signal all-complete only after it finishes.
            _autoPublishPage.OnPublishComplete = () =>
            {
                // Publish-to-Intune script has finished — NOW show the "Publish — Complete"
                // status dialog (when ShowMessageBox = Yes), since this is the true end of
                // the publish step for PublishToIntune=Yes. OnPublishComplete is invoked from
                // a background task's finally, so marshal to the UI thread before ShowDialog.
                Action showAndComplete = () =>
                {
                    // Resolve the real publish outcome once - used for the status dialog and,
                    // critically, to route to the success path (documents + App Owner mail) or the
                    // failure path (status only). A cancelled/denied sign-in leaves this Failed.
                    var pubState = AutoFlowStatus.Get(AutoFlowStatus.PublishToIntune);
                    bool publishOk = pubState == AutoFlowStatus.State.Ok;

                    // "Add supersedence automatically" has no step of its own: the publish script
                    // queues the previous versions as part of this upload, so it succeeds or fails
                    // with the publish. Recorded only when the box was ticked for this run, so an
                    // app with it switched off stays neutral instead of painting green.
                    if (AutoFlowStatus.IsWanted(AutoFlowStatus.AddSupersedence))
                        AutoFlowStatus.Set(AutoFlowStatus.AddSupersedence,
                            publishOk ? AutoFlowStatus.State.Ok : AutoFlowStatus.State.Failed);

                    // Surface the reason in the Download Information column too. The publish output
                    // lives on the Publish tab's Log, but a failed publish aborts the flow straight
                    // back to the Download tab - so without this the run ends on a screen that says
                    // only "Failed" and the actual error is a tab away.
                    if (!publishOk)
                    {
                        string pubLog = _autoPublishPage?.PublishLogPath ?? "";
                        string pubErr = ReadPublishError(pubLog);
                        ReportPackageErrorToDownloadTab(
                            "Publish to intune failed",
                            string.IsNullOrWhiteSpace(pubErr) ? "The publish script did not complete." : pubErr,
                            string.IsNullOrWhiteSpace(pubLog)
                                ? "Open the Publish tab, Log for the full output."
                                : "Full output: " + pubLog);
                    }
                    if (AutoShowMessageBox.Value)
                    {
                        try
                        {
                            var st = _autoPublishPage?.PublishStartTime ?? DateTime.Now;
                            var en = _autoPublishPage?.PublishEndTime   ?? DateTime.Now;
                            var el = en - st;
                            string es = el.TotalMinutes >= 1 ? $"{(int)el.TotalMinutes}m {el.Seconds}s"
                                      : el.TotalSeconds >= 1 ? $"{(int)el.TotalSeconds}s" : $"{(int)el.TotalMilliseconds}ms";
                            string logP = _autoPublishPage?.PublishLogPath ?? "";
                            string statusText = publishOk ? "Published to Intune"
                                              : pubState == AutoFlowStatus.State.Failed ? "Publish failed"
                                              : "Publish not completed";
                            string pubTitle = publishOk ? "Publish - Complete" : "Publish - Failed";
                            var rows = new System.Collections.Generic.List<(string,string,bool)>
                            {
                                ("Time", $"Started: {st:HH:mm:ss}  Finished: {en:HH:mm:ss}  Duration: {es}", false),
                                ("App",  _autoPublishPage?.AppName ?? "", false),
                                ("Status", statusText, publishOk),
                            };
                            if (!string.IsNullOrWhiteSpace(logP)) rows.Add(("Log", logP, false));
                            AutoStatusDialog.Show(this, pubTitle, "🚀", publishOk, rows, logToOpen: logP);
                        }
                        catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    }
                    if (_publishToIntuneIsTerminus)
                    {
                        _publishToIntuneIsTerminus = false;
                        if (publishOk) RaiseAutoFlowComplete();   // success -> documents + App Owner mail + status
                        else           AbortAutoFlow();           // publish failed -> status panel only, no "ready" mail
                    }
                };
                if (Dispatcher.CheckAccess()) showAndComplete();
                else Dispatcher.Invoke(showAndComplete);
            };
            FramePublish?.Navigate(_autoPublishPage);
        }

        // True when an Intune publish (PublishToIntune=Yes) will run as the final
        // step. While set, the Publish-Save point must NOT raise all-complete — the
        // publish-script completion does.
        private bool _publishToIntuneIsTerminus = false;

        /// <summary>
        /// Pulls the one line worth showing out of a failed publish run.
        ///
        /// AutoPackage_PublishToIntune.ps1 writes the reason as "ERROR: ..." and the caller then
        /// adds "Publish failed with exit code: n", so the last ERROR line is the specific one and
        /// the exit-code line is the fallback when the failure produced no ERROR at all. Scanned
        /// backwards and capped, because a publish log runs to hundreds of lines and only the end
        /// describes the failure.
        /// </summary>
        private static string ReadPublishError(string logPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(logPath) || !System.IO.File.Exists(logPath)) return "";

                var lines = System.IO.File.ReadAllLines(logPath);
                string errLine = "", exitLine = "";

                for (int i = lines.Length - 1; i >= 0 && i >= lines.Length - 400; i--)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0) continue;

                    // Drop a leading "[HH:mm:ss] " stamp so both log styles match the same way.
                    int close = t.IndexOf(']');
                    if (t.StartsWith("[") && close > 0 && close < 12) t = t.Substring(close + 1).Trim();

                    if (errLine.Length == 0 && t.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                    {
                        errLine = t;
                        break;   // most specific line available - stop here
                    }
                    if (exitLine.Length == 0 &&
                        t.IndexOf("Publish failed with exit code", StringComparison.OrdinalIgnoreCase) >= 0)
                        exitLine = t;
                }

                string pick = errLine.Length > 0 ? errLine : exitLine;
                return pick.Length > 300 ? pick.Substring(0, 300) + "..." : pick;
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            return "";
        }

        // Surface a failed step in the Download tab's Download Information column. Reflection keeps
        // this page free of a compile-time dependency on SourceDownloadPage, the same way
        // FrameDownload is populated. No-op when that page is not loaded.
        private static void ReportPackageErrorToDownloadTab(string title, string message, string hint)
        {
            try
            {
                var sdType = System.Type.GetType("SwiftPack.Views.SourceDownloadPage") ??
                    AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return System.Array.Empty<Type>(); } })
                        .FirstOrDefault(t => t.Name == "SourceDownloadPage");
                var mi = sdType?.GetMethod("ReportPackageError",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                mi?.Invoke(null, new object?[] { title, message, hint });
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        // ── Intunewin tab: AutoIntunewinPage (self-contained, no dependency on CreateIntunewinPage) ──
        private AutoIntunewinPage? _autoIntunewinPage = null;

        private void EnsureIntunewinPageLoaded()
        {
            if (_autoIntunewinPage != null) return;
            _autoIntunewinPage = new AutoIntunewinPage();
            _autoIntunewinPage.SetContext(new AutoIntunewinContext(this));
            FrameIntunewin?.Navigate(_autoIntunewinPage);
        }

        public void NavigateToExecuteTab(
            string scriptPath,
            string installPackage   = "No",
            string uninstallPackage = "No",
            Action? onAfterExecute  = null,
            Action? onAfterInstall  = null)
        {
            void DoNavigate()
            {
                EnsureAutoExecPageLoaded();
                if (_autoExecPage == null) return;

                // Point the auto-flow Icon.png at the per-app folder and, once it exists, wire
                // entry.Logo so the Publish step sends the 256px icon rather than nothing.
                _autoExecPage.SetAppLogoTarget(_autoLogoPerAppFolder, _staticSelectedApp?.SourceAppName ?? "", dest =>
                {
                    if (_staticSelectedApp != null && string.IsNullOrWhiteSpace(_staticSelectedApp.Logo))
                        _staticSelectedApp.Logo = dest;
                });

                // Hand the app's configured PSADT Install/Uninstall commands to the Execute page so
                // its PSADT Install / PSADT Uninstall boxes show the resolved commands.
                _autoExecPage.SetPsadtCommands(_staticSelectedApp?.PSADTInstallCommand ?? "", _staticSelectedApp?.PSADTUninstallCommand ?? "");

                // Open Application (Package settings): when Yes, the Execute page opens the
                // installed app's main exe after a successful Install, holds it briefly, then
                // closes it before the next step. The app name drives shortcut resolution.
                _autoExecPage.SetOpenApplication(
                    _staticSelectedApp?.OpenApplication ?? "No",
                    !string.IsNullOrWhiteSpace(_staticSelectedApp?.SourceAppName) ? _staticSelectedApp!.SourceAppName : (_staticSelectedApp?.IntuneAppName ?? ""));

                if (!string.IsNullOrWhiteSpace(scriptPath))
                    _autoExecPage.SetScriptPath(scriptPath);

                ActivateTopTab(activeTab: "Execute");

                bool doInstall   = !installPackage.Equals("No",   StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(installPackage);
                bool doUninstall = !uninstallPackage.Equals("No", StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(uninstallPackage);

                if (!doInstall && !doUninstall)
                {
                    onAfterExecute?.Invoke();
                    return;
                }

                bool useSystemContextInstall   = installPackage.Equals("System Context",   StringComparison.OrdinalIgnoreCase);
                bool useSystemContextUninstall = uninstallPackage.Equals("System Context", StringComparison.OrdinalIgnoreCase);

                void RunInstall(Action? after)
                {
                    if (!string.IsNullOrWhiteSpace(scriptPath))
                        _autoExecPage!.SetScriptAndInstall(scriptPath, after, useSystemContextInstall);
                }
                void RunUninstall(Action? after)
                {
                    if (!string.IsNullOrWhiteSpace(scriptPath))
                        _autoExecPage!.SetScriptAndUninstall(scriptPath, after, useSystemContextUninstall);
                }

                if (doInstall && doUninstall)
                {
                    // Install completes → fire onAfterInstall (app still installed — registry populated)
                    // → 2-second delay → Uninstall → onAfterExecute (Intunewin → Publish)
                    RunInstall(() =>
                    {
                        // Fire onAfterInstall synchronously before the delay so the
                        // Publish tab is pre-filled while the app is still installed.
                        onAfterInstall?.Invoke();
                        System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
                            Dispatcher.Invoke(() => RunUninstall(onAfterExecute)));
                    });
                }
                else if (doInstall)
                    RunInstall(onAfterExecute);
                else
                    RunUninstall(onAfterExecute);
            }

            if (Dispatcher.CheckAccess())
                DoNavigate();
            else
                Dispatcher.Invoke(DoNavigate);
        }


        // ══════════════════════════════════════════════════════════════════
        //  WriteIntuneAppSettingsJson
        //  Writes ALL Intune & Deployment fields to the package-specific
        //  Intune JSON file: <PackageFolder>\Intune\<PackageName>.json
        //  This file captures the runtime-resolved values (including
        //  auto-populated detection fields) WITHOUT modifying the per-app
        //  template JSON (e.g. Smekal_FileCompare.json).
        // ══════════════════════════════════════════════════════════════════
        public void WriteIntuneAppSettingsJson(
            string packageFolder,
            string? overrideName    = null,
            string? overrideVendor  = null,
            string? overrideVersion = null,
            string? intunewinPath   = null,
            bool    alwaysWrite     = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(packageFolder) || !Directory.Exists(packageFolder))
                    return;

                // Only create the Intune folder and write the JSON when the user has
                // explicitly enabled PublishSettings or PublishToIntune.
                // CreateIntunewin alone does NOT write the JSON — the .intunewin file
                // is created by AutoIntunewinPage independently.
                // Pass alwaysWrite:true from the manual Save button to bypass this guard.
                if (!alwaysWrite)
                {
                    bool publishEnabled = (_cfg?.PublishToIntune  ?? false)
                                      || (_staticSelectedApp?.PublishToIntune == "Yes")
                                      || (_staticSelectedApp?.PublishSettings == "Yes");
                    if (!publishEnabled)
                        return;
                }

                // Intune folder is a sibling of the app-package folder (parent of the ps1's
                // folder), matching where AutoIntunewinPage writes the .intunewin.
                string intunePath = IOPath.Combine(ResolveOutputBase(packageFolder), "Intune");
                if (!Directory.Exists(intunePath))
                    Directory.CreateDirectory(intunePath);

                // Package name = folder name of packageFolder (NOT the output base — the JSON
                // and the .intunewin are both named after the package folder).
                string pkgName = IOPath.GetFileName(packageFolder.TrimEnd(IOPath.DirectorySeparatorChar));
                string jsonPath = IOPath.Combine(intunePath, pkgName + ".json");

                var p  = _autoPublishPage;
                var sel = _staticSelectedApp;   // per-app template values (may have empties)

                // Helper: prefer publish-page value, fall back to _staticSelectedApp
                string V(string? pub, string? sel2) =>
                    !string.IsNullOrWhiteSpace(pub)  ? pub!  :
                    !string.IsNullOrWhiteSpace(sel2) ? sel2! : "";

                // Intune's Management Extension runs Win32 install/uninstall commands as a 32-bit
                // process, so a bare "powershell.exe" resolves to the SysWOW64 (32-bit) host. That
                // host applies WOW64 registry redirection, so a script that writes an explicit
                // HKLM\SOFTWARE\WOW6432Node\... key lands under WOW6432Node\WOW6432Node and the ARP
                // flag (NoRemove/NoModify/NoRepair) never appears on the real entry. Rewriting the
                // launcher to the Sysnative alias forces the 64-bit PowerShell, which writes the
                // literal path correctly. Applied ONLY to the command Intune runs - the manual
                // Execute is already 64-bit and keeps the bare launcher (Sysnative does not resolve
                // for a 64-bit caller). A command that already carries a path, or a source-installer
                // command that is not powershell.exe at all, is left exactly as it is.
                string ToIntunePwsh(string? cmd)
                {
                    string c = cmd ?? "";
                    string t = c.TrimStart();
                    const string bare = "powershell.exe";
                    if (!t.StartsWith(bare, StringComparison.OrdinalIgnoreCase)) return c;
                    int i = c.IndexOf(bare, StringComparison.OrdinalIgnoreCase);
                    return c.Substring(0, i)
                         + @"%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
                         + c.Substring(i + bare.Length);
                }

                // Build the detection section from AutoPublishPage.DetectionRules
                // (these contain the runtime-resolved, auto-populated values).
                var rules = p?.DetectionRules ?? new System.Collections.Generic.List<AutoDetectionRule>();
                var msi  = rules.FirstOrDefault(r => r.RuleType == "MSI");
                var file = rules.FirstOrDefault(r => r.RuleType == "File");
                var reg  = rules.FirstOrDefault(r => r.RuleType == "Registry");

                var doc = new System.Collections.Generic.Dictionary<string, object?>
                {
                    // ── App Info ──────────────────────────────────────────
                    ["IntuneAppName"]             = V(overrideName    ?? p?.AppName,   sel?.IntuneAppName),
                    ["IntuneAppVendor"]           = V(overrideVendor  ?? p?.AppVendor, sel?.IntuneAppVendor),
                    ["IntuneAppVersion"]          = V(overrideVersion ?? p?.AppVersion,sel?.IntuneAppVersion),
                    ["IntuneCategory"]            = V(p?.Category,    sel?.IntuneCategory),
                    ["FeaturedApp"]               = V(p?.FeaturedApp, sel?.FeaturedApp),
                    ["Description"]               = V(p?.Description, sel?.Description),
                    ["Developer"]                 = V(p?.Developer,   sel?.Developer),
                    ["Owner"]                     = V(p?.Owner,       sel?.Owner),
                    ["InformationURL"]            = V(p?.InformationURL, sel?.InformationURL),
                    ["PrivacyURL"]                = V(p?.PrivacyURL,  sel?.PrivacyURL),
                    ["Notes"]                     = V(p?.Notes,       sel?.Notes),
                    ["Logo"]                      = V(p?.IconPath,    sel?.Logo),
                    // ── Program ───────────────────────────────────────────
                    ["InstallCommand"]            = ToIntunePwsh(V(p?.InstallCmd,             sel?.InstallCommand)),
                    ["UninstallCommand"]          = ToIntunePwsh(V(p?.UninstallCmd,           sel?.UninstallCommand)),
                    ["AllowAvailableUninstall"]   = V(p?.AllowAvailableUninstall,sel?.AllowAvailableUninstall),
                    ["InstallBehavior"]           = V(p?.InstallBehavior,        sel?.InstallBehavior),
                    ["InstallationTime"]          = V(p?.InstallationTime,       sel?.InstallationTime),
                    ["DeviceRestartBehaviour"]    = V(p?.DeviceRestartBehavior,  sel?.DeviceRestartBehaviour),
                    // ── Requirements ──────────────────────────────────────
                    ["MinimumOperatingSystem"]    = V(p?.MinimumOperatingSystem,       sel?.MinimumOperatingSystem),
                    ["OperatingSystemArchitecture"]= V(p?.OperatingSystemArchitecture, sel?.OperatingSystemArchitecture),
                    // ── Detection ─────────────────────────────────────────
                    ["RulesFormat"]               = V(p?.RulesFormat, sel?.RulesFormat),
                    ["DetectionTypeMSI"]          = msi != null ? "Yes" : "No",
                    ["DetectionTypeFile"]         = sel?.DetectionTypeFile     ?? "No",
                    ["DetectionTypeRegistry"]     = sel?.DetectionTypeRegistry ?? "No",
                    // MSI detection (runtime-resolved product code from AutoPublishPage)
                    ["DetectionMsiProductCode"]   = msi?.MsiProductCode ?? sel?.DetectionMsiProductCode ?? "",
                    ["DetectionMsiVersionCheck"]  = msi != null ? (msi.MsiCheck.Equals("Yes", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No") : (sel?.DetectionMsiVersionCheck ?? ""),
                    // Operator + Value persisted ONLY when a version check is requested,
                    // mirroring what the Detection tab shows (no confusing stale data).
                    ["DetectionMsiOperator"]      = (msi != null && msi.MsiCheck.Equals("Yes", StringComparison.OrdinalIgnoreCase)) ? msi.MsiOperator : "",
                    ["DetectionMsiValue"]         = (msi != null && msi.MsiCheck.Equals("Yes", StringComparison.OrdinalIgnoreCase)) ? msi.MsiVersion  : "",
                    // File detection (runtime-resolved from AutoPublishPage)
                    ["DetectionFileFolderPath"]   = file?.FilePath     ?? sel?.DetectionFileFolderPath ?? "",
                    ["DetectionFileFileName"]     = file?.FileName     ?? sel?.DetectionFileFileName  ?? "",
                    ["DetectionFileDetectionMethod"]    = file?.FileCheck    ?? sel?.DetectionFileDetectionMethod ?? "",
                    // Operator persisted ONLY for comparison methods (not "exists").
                    ["DetectionFileOperator"]     = (file != null && !AutoPublishPage.FileMethodIsExistence(file.FileCheck)) ? file.FileOperator : "",
                    // For Date created/modified the value is split into separate date + time
                    // fields; for String/Size it is the plain value. "Exists" has no value.
                    ["DetectionFileValue"]        = (file != null && !AutoPublishPage.FileMethodIsExistence(file.FileCheck)
                                                        && !(file.FileCheck ?? "").StartsWith("Date", StringComparison.OrdinalIgnoreCase))
                                                     ? file.FileValue : "",
                    ["DetectionFileDateValue"]    = !string.IsNullOrWhiteSpace(p?.DetectionFileDateValue) ? p!.DetectionFileDateValue : (sel?.DetectionFileDateValue ?? ""),
                    ["DetectionFileTimeValue"]    = !string.IsNullOrWhiteSpace(p?.DetectionFileTimeValue) ? p!.DetectionFileTimeValue : (sel?.DetectionFileTimeValue ?? ""),
                    // Size-in-MB value, resolved from the installed exe after a successful
                    // install. Persisted only for the "Size in MB" method; falls back to the
                    // previously stored DetectionFileSizeValue when not freshly resolved.
                    ["DetectionFileSizeValue"]    = (file != null && (file.FileCheck ?? "").Equals("Size in MB", StringComparison.OrdinalIgnoreCase))
                                                     ? (!string.IsNullOrWhiteSpace(p?.DetectionFileSizeValue) ? p!.DetectionFileSizeValue : file.FileValue)
                                                     : (sel?.DetectionFileSizeValue ?? ""),
                    // Pre-converted UTC instant (ISO-8601) of the Date created/modified value.
                    // The local date+time SwiftPack displays is converted to UTC here so the
                    // publish script can send Intune the correct value. Intune stores detection
                    // date-times in UTC and renders them in the viewer's local zone; sending the
                    // raw local time (as if it were UTC) shifted the portal value by the local
                    // offset. Empty when the method isn't a Date method or the value is unset.
                    ["DetectionFileDateUtc"]      = BuildDetectionFileDateUtc(
                                                        file?.FileCheck ?? sel?.DetectionFileDetectionMethod ?? "",
                                                        (!string.IsNullOrWhiteSpace(p?.DetectionFileDateValue) ? p!.DetectionFileDateValue : (sel?.DetectionFileDateValue ?? "")),
                                                        (!string.IsNullOrWhiteSpace(p?.DetectionFileTimeValue) ? p!.DetectionFileTimeValue : (sel?.DetectionFileTimeValue ?? ""))),
                    ["DetectionFile32Bit"]        = file != null ? (file.File32Bit ? "Yes" : "No") : (sel?.DetectionFile32Bit ?? ""),
                    // Registry detection (runtime-resolved from AutoPublishPage)
                    ["DetectionRegKeyPath"]       = reg?.RegKeyPath   ?? sel?.DetectionRegKeyPath    ?? "",
                    // Value name persisted only when it applies (not a key-level check).
                    ["DetectionRegValueName"]     = (reg != null && !AutoPublishPage.RegMethodIsKeyLevel(reg.RegCheck)) ? reg.RegValue : "",
                    ["DetectionRegDetectionMethod"]     = reg?.RegCheck     ?? sel?.DetectionRegDetectionMethod  ?? "",
                    // Operator + Value persisted only for a value comparison with a value name.
                    ["DetectionRegOperator"]      = (reg != null && AutoPublishPage.RegShowsOperatorAndValue(reg.RegCheck, reg.RegValue)) ? reg.RegOperator   : "",
                    ["DetectionRegistryValue"]    = (reg != null && AutoPublishPage.RegShowsOperatorAndValue(reg.RegCheck, reg.RegValue)) ? reg.RegValueData : "",
                    ["DetectionReg32Bit"]         = reg != null ? (reg.Reg32Bit ? "Yes" : "No") : (sel?.DetectionReg32Bit ?? ""),
                    // Script detection
                    ["DetectionScriptFile"]       = p?.DetectionScriptFile  ?? sel?.DetectionScriptFile ?? "",
                    ["DetectionScriptEnforceSignatureCheck"] = p != null ? (p.DetectionScriptEnforceSignature ? "Yes" : "No") : (sel?.DetectionScriptEnforceSignatureCheck ?? ""),
                    ["DetectionScriptRunAs32Bit"] = p != null ? (p.DetectionScriptRunAs32Bit ? "Yes" : "No") : (sel?.DetectionScriptRunAs32Bit ?? ""),
                    // ── Assignments ───────────────────────────────────────
                    ["DeploymentType"]            = V(p?.DeploymentType, sel?.DeploymentType),
                    ["AssignmentsAvailable"]      = sel?.AssignmentsAvailable ?? "",
                    // App-owner UAT notification mail (read back by SendAppOwnerMail after publish).
                    ["AppOwnerEmail"]             = sel?.AppOwnerEmail ?? "",
                    ["SendMailToAppOwner"]        = string.IsNullOrWhiteSpace(sel?.SendMailToAppOwner) ? "No" : sel!.SendMailToAppOwner,
                    ["MailSubject"]               = sel?.MailSubject ?? "",
                    ["MailBody"]                  = sel?.MailBody ?? "",
                    // Read by the AutoSupersede block of AutoPackage_PublishToIntune.ps1. Written
                    // even when it is the default, because a missing key means Yes there and an
                    // app the user switched off would otherwise silently supersede again.
                    ["AddSupersedenceAutomatically"] = string.IsNullOrWhiteSpace(sel?.AddSupersedenceAutomatically) ? "Yes" : sel!.AddSupersedenceAutomatically,
                    // Dependencies / supersedences are added below as dynamic numbered keys.
                    // ── Intunewin file path ────────────────────────────────
                    ["IntunewinFilePath"]         = intunewinPath ?? p?.IntunewinFilePath ?? "",
                };

                // Dynamic dependencies / supersedences as numbered keys (App ID based).
                for (int di = 0; di < (sel?.Dependencies?.Count ?? 0); di++)
                {
                    var d = sel!.Dependencies[di]; int n = di + 1;
                    if (string.IsNullOrWhiteSpace(d.AppName) && string.IsNullOrWhiteSpace(d.AppID)) continue;
                    doc[$"DependencyAppName{n}"]     = d.AppName ?? "";
                    doc[$"DependencyAppID{n}"]       = d.AppID ?? "";
                    doc[$"DependencyAutoInstall{n}"] = string.IsNullOrWhiteSpace(d.AutoInstall) ? "No" : d.AutoInstall;
                }
                for (int si = 0; si < (sel?.Supersedences?.Count ?? 0); si++)
                {
                    var sp = sel!.Supersedences[si]; int n = si + 1;
                    if (string.IsNullOrWhiteSpace(sp.AppName) && string.IsNullOrWhiteSpace(sp.AppID)) continue;
                    doc[$"SupersedenceAppName{n}"]                  = sp.AppName ?? "";
                    doc[$"SupersedenceAppID{n}"]                    = sp.AppID ?? "";
                    doc[$"SupersedenceUninstallPreviousVersion{n}"] = string.IsNullOrWhiteSpace(sp.UninstallPreviousVersion) ? "No" : sp.UninstallPreviousVersion;
                }

                string json = JsonSerializer.Serialize(doc,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        // Req 5/6: write literal characters (e.g. " ) instead of \u0022 escapes.
                        // The default System.Text.Json encoder escapes ", &, <, > etc. for HTML
                        // safety; the relaxed encoder keeps them readable in this on-disk JSON.
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                File.WriteAllText(jsonPath, json, new System.Text.UTF8Encoding(false));

                // Remember these for the Document tab so it can populate reliably even when
                // opened standalone later in the session.
                _docIntuneJsonPath = jsonPath;
                if (!string.IsNullOrWhiteSpace(intunewinPath)) _docIntunewinPath = intunewinPath;
                // Fresh package data exists now — allow the Document tab to populate again.
                _documentSuppressed = false;
                AutoFlowStatus.Set(AutoFlowStatus.PublishSettings, AutoFlowStatus.State.Ok);
            }
            catch { /* non-critical - never crash the publish flow */ }
        }

        // Convert the locally-displayed Date created/modified value (MM/dd/yyyy + h:mm:ss tt,
        // both in the machine's local time zone, as read from the file system) into a UTC
        // ISO-8601 instant for Intune (e.g. "2025-12-31T07:16:38Z"). Returns "" when the
        // detection method isn't a Date method or the inputs can't be parsed. This avoids the
        // portal showing a time shifted by the local UTC offset.
        private static string BuildDetectionFileDateUtc(string method, string dateStr, string timeStr)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(method) ||
                    !method.StartsWith("Date", StringComparison.OrdinalIgnoreCase))
                    return "";
                if (string.IsNullOrWhiteSpace(dateStr)) return "";

                string combined = (dateStr.Trim() + " " + (timeStr ?? "").Trim()).Trim();
                // Accept the formats SwiftPack writes plus a couple of common variants.
                string[] fmts =
                {
                    "MM/dd/yyyy hh:mm:ss tt", "MM/dd/yyyy h:mm:ss tt",
                    "MM-dd-yyyy hh:mm:ss tt", "MM-dd-yyyy h:mm:ss tt",
                    "MM/dd/yyyy HH:mm:ss",    "MM-dd-yyyy HH:mm:ss",
                    "MM/dd/yyyy",             "MM-dd-yyyy",
                };
                DateTime localDt;
                bool parsed = DateTime.TryParseExact(combined, fmts,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out localDt);
                if (!parsed)
                {
                    // Fallback: lenient parse in the local culture/zone.
                    if (!DateTime.TryParse(combined, System.Globalization.CultureInfo.CurrentCulture,
                            System.Globalization.DateTimeStyles.AssumeLocal, out localDt))
                        return "";
                }
                // The file-system time is local; convert to UTC for Intune.
                DateTime utc = localDt.Kind == DateTimeKind.Utc ? localDt : localDt.ToUniversalTime();
                return utc.ToString("yyyy-MM-ddTHH:mm:ssZ",
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return ""; }
        }

        /// When prefillOnly=true, populates all UI fields (including registry detection data)
        /// into memory but does NOT write the Intune JSON, does NOT switch to the Publish tab,
        /// and does NOT trigger Save. Used to capture registry detection data immediately after
        /// Install (while app is still installed) before Uninstall runs. The JSON is only
        /// written when this method is called with prefillOnly=false (the real Publish tab open).
        /// </summary>
        public void NavigateToPublishTab(
            string packageFolder,
            string appName    = "",
            string appVendor  = "",
            string appVersion = "",
            string description = "",
            bool   prefillOnly = false)
        {
            void DoNavigate()
            {
                EnsureAutoPublishPageLoaded();
                if (_autoPublishPage == null) return;

                // Per-app JSON — load from pending template folder (same source as before)
                AutoPackageAppEntry? perApp = null;
                try
                {
                    string tplFolder = _pendingTemplateFolder;
                    if (!string.IsNullOrWhiteSpace(tplFolder) && Directory.Exists(tplFolder))
                    {
                        string? jsonPath = Directory.GetFiles(tplFolder, "*.json", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault();
                        if (jsonPath != null && File.Exists(jsonPath))
                            perApp = System.Text.Json.JsonSerializer.Deserialize<AutoPackageAppEntry>(
                                File.ReadAllText(jsonPath),
                                new System.Text.Json.JsonSerializerOptions
                                    { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true });
                    }
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                // Capture the MSI product code AS PROVIDED by the user — from the per-app
                // template JSON (perApp) OR the live selected-app data (_staticSelectedApp,
                // which reflects values just typed in the Download tab's Package Information).
                // This lets us honour an explicit user-entered product code even for an EXE
                // source, while still clearing a STALE code that only came from auto-resolution.
                string jsonFileMsiProductCode =
                    !string.IsNullOrWhiteSpace(perApp?.DetectionMsiProductCode) ? perApp!.DetectionMsiProductCode :
                    !string.IsNullOrWhiteSpace(_staticSelectedApp?.DetectionMsiProductCode) ? _staticSelectedApp!.DetectionMsiProductCode :
                    "";

                // Prefer _staticSelectedApp (live JSON data) for detection fields over template perApp
                if (_staticSelectedApp != null && perApp != null)
                {
                    // Merge: _staticSelectedApp wins for detection + display fields
                    perApp.RulesFormat                          = _staticSelectedApp.RulesFormat                          ?? perApp.RulesFormat;
                    perApp.DetectionTypeMSI                     = _staticSelectedApp.DetectionTypeMSI                     ?? perApp.DetectionTypeMSI;
                    perApp.DetectionTypeFile                    = _staticSelectedApp.DetectionTypeFile                    ?? perApp.DetectionTypeFile;
                    perApp.DetectionTypeRegistry                = _staticSelectedApp.DetectionTypeRegistry                ?? perApp.DetectionTypeRegistry;
                    perApp.DetectionTypeScript                  = _staticSelectedApp.DetectionTypeScript                  ?? perApp.DetectionTypeScript;
                    perApp.DetectionScriptFile                  = _staticSelectedApp.DetectionScriptFile                  ?? perApp.DetectionScriptFile;
                    perApp.DetectionScriptEnforceSignatureCheck = _staticSelectedApp.DetectionScriptEnforceSignatureCheck ?? perApp.DetectionScriptEnforceSignatureCheck;
                    perApp.DetectionScriptRunAs32Bit            = _staticSelectedApp.DetectionScriptRunAs32Bit            ?? perApp.DetectionScriptRunAs32Bit;
                    // Use non-empty-string merge (NE) so that an empty string in _staticSelectedApp
                    // does not overwrite a non-empty value already in perApp (from template JSON).
                    static string NE(string? a, string? b) => !string.IsNullOrWhiteSpace(a) ? a! : (b ?? "");
                    // PREFILL FLOW: the File-folder/File-name/File-value and Registry
                    // key/value/value-name fields are AUTO-PREFILL data. A typed Download-tab
                    // value is written to the per-app template JSON immediately, so perApp
                    // already reflects user input — it always WINS here. When perApp is empty,
                    // fall back to _staticSelectedApp, which carries the values captured by the
                    // prefillOnly pass (run right after Create Package, while the source app was
                    // still installed). Without this fallback the Publish → Detection tab renders
                    // the rules with empty folder/file/key details even though the capture
                    // succeeded and the Intune json gets the full values. This merged perApp is
                    // the publish-time view only — Publish Save writes Intune\PackageName.json,
                    // never the per-app template JSON, so captured values still do not leak into
                    // the template.
                    perApp.DetectionFileFolderPath              = NE(perApp.DetectionFileFolderPath, _staticSelectedApp.DetectionFileFolderPath);
                    perApp.DetectionFileFileName                = NE(perApp.DetectionFileFileName,   _staticSelectedApp.DetectionFileFileName);
                    perApp.DetectionFileDetectionMethod               = NE(_staticSelectedApp.DetectionFileDetectionMethod,               perApp.DetectionFileDetectionMethod);
                    perApp.DetectionFileOperator                = NE(_staticSelectedApp.DetectionFileOperator,                perApp.DetectionFileOperator);
                    perApp.DetectionFileValue                   = NE(perApp.DetectionFileValue, _staticSelectedApp.DetectionFileValue);
                    perApp.DetectionFileSizeValue               = NE(_staticSelectedApp.DetectionFileSizeValue,               perApp.DetectionFileSizeValue);
                    perApp.DetectionFileDateValue               = NE(_staticSelectedApp.DetectionFileDateValue,               perApp.DetectionFileDateValue);
                    perApp.DetectionFileTimeValue               = NE(_staticSelectedApp.DetectionFileTimeValue,               perApp.DetectionFileTimeValue);
                    perApp.DetectionFile32Bit                   = NE(_staticSelectedApp.DetectionFile32Bit,                   perApp.DetectionFile32Bit);
                    perApp.DetectionRegKeyPath                  = NE(perApp.DetectionRegKeyPath,   _staticSelectedApp.DetectionRegKeyPath);
                    perApp.DetectionRegValueName                = NE(perApp.DetectionRegValueName, _staticSelectedApp.DetectionRegValueName);
                    perApp.DetectionRegDetectionMethod                = NE(_staticSelectedApp.DetectionRegDetectionMethod,                perApp.DetectionRegDetectionMethod);
                    perApp.DetectionRegOperator                 = NE(_staticSelectedApp.DetectionRegOperator,                 perApp.DetectionRegOperator);
                    perApp.DetectionRegistryValue               = NE(perApp.DetectionRegistryValue, _staticSelectedApp.DetectionRegistryValue);
                    perApp.DetectionMsiVersionCheck             = NE(_staticSelectedApp.DetectionMsiVersionCheck,             perApp.DetectionMsiVersionCheck);
                    perApp.DetectionMsiOperator                 = NE(_staticSelectedApp.DetectionMsiOperator,                 perApp.DetectionMsiOperator);
                    perApp.DetectionMsiValue                    = NE(_staticSelectedApp.DetectionMsiValue,                    perApp.DetectionMsiValue);
                    perApp.DetectionMsiProductCode              = NE(_staticSelectedApp.DetectionMsiProductCode,              perApp.DetectionMsiProductCode);
                    perApp.Description                          = _staticSelectedApp.Description                          ?? perApp.Description;
                    perApp.InformationURL                       = _staticSelectedApp.InformationURL                       ?? perApp.InformationURL;
                    perApp.PrivacyURL                           = _staticSelectedApp.PrivacyURL                          ?? perApp.PrivacyURL;
                    perApp.Developer                            = _staticSelectedApp.Developer                            ?? perApp.Developer;
                    perApp.Owner                                = _staticSelectedApp.Owner                                ?? perApp.Owner;
                    perApp.Notes                                = _staticSelectedApp.Notes                                ?? perApp.Notes;
                    perApp.Logo                                 = _staticSelectedApp.Logo                                 ?? perApp.Logo;
                    perApp.IntuneCategory                       = _staticSelectedApp.IntuneCategory                      ?? perApp.IntuneCategory;
                    perApp.FeaturedApp                          = _staticSelectedApp.FeaturedApp                         ?? perApp.FeaturedApp;
                    perApp.InstallCommand                       = _staticSelectedApp.InstallCommand                       ?? perApp.InstallCommand;
                    perApp.SourceInstallCommand                 = !string.IsNullOrWhiteSpace(_staticSelectedApp.SourceInstallCommand)   ? _staticSelectedApp.SourceInstallCommand   : perApp.SourceInstallCommand;
                    perApp.SourceUninstallCommand               = !string.IsNullOrWhiteSpace(_staticSelectedApp.SourceUninstallCommand) ? _staticSelectedApp.SourceUninstallCommand : perApp.SourceUninstallCommand;
                    perApp.PSADTTemplateFolder                  = !string.IsNullOrWhiteSpace(_staticSelectedApp.PSADTTemplateFolder)    ? _staticSelectedApp.PSADTTemplateFolder    : perApp.PSADTTemplateFolder;
                    perApp.PSADTInstallCommand                  = !string.IsNullOrWhiteSpace(_staticSelectedApp.PSADTInstallCommand)    ? _staticSelectedApp.PSADTInstallCommand    : perApp.PSADTInstallCommand;
                    perApp.PSADTUninstallCommand                = !string.IsNullOrWhiteSpace(_staticSelectedApp.PSADTUninstallCommand)  ? _staticSelectedApp.PSADTUninstallCommand  : perApp.PSADTUninstallCommand;
                    perApp.DownloadPreference                   = !string.IsNullOrWhiteSpace(_staticSelectedApp.DownloadPreference)     ? _staticSelectedApp.DownloadPreference     : perApp.DownloadPreference;
                    perApp.UninstallCommand                     = _staticSelectedApp.UninstallCommand                     ?? perApp.UninstallCommand;
                    perApp.AllowAvailableUninstall              = _staticSelectedApp.AllowAvailableUninstall              ?? perApp.AllowAvailableUninstall;
                    perApp.InstallBehavior                      = _staticSelectedApp.InstallBehavior                      ?? perApp.InstallBehavior;
                    perApp.DeviceRestartBehaviour               = _staticSelectedApp.DeviceRestartBehaviour               ?? perApp.DeviceRestartBehaviour;
                    perApp.MinimumOperatingSystem               = _staticSelectedApp.MinimumOperatingSystem               ?? perApp.MinimumOperatingSystem;
                    perApp.OperatingSystemArchitecture          = _staticSelectedApp.OperatingSystemArchitecture          ?? perApp.OperatingSystemArchitecture;
                    perApp.DeploymentType                       = _staticSelectedApp.DeploymentType                       ?? perApp.DeploymentType;
                    perApp.AssignmentsRequired                  = _staticSelectedApp.AssignmentsRequired                  ?? perApp.AssignmentsRequired;
                    perApp.AssignmentsAvailable                 = _staticSelectedApp.AssignmentsAvailable                 ?? perApp.AssignmentsAvailable;
                    perApp.AssignmentsUninstall                 = _staticSelectedApp.AssignmentsUninstall                 ?? perApp.AssignmentsUninstall;
                }
                else if (_staticSelectedApp != null)
                {
                    perApp = _staticSelectedApp;
                }

                // ── Fallback: read detection values from AutoPackagePage UI boxes ──────
                // When perApp still has empty detection fields (EXE app, no registry hit at
                // download time), the AutoPackagePage UI text boxes may already be populated
                // from the LookupInstalledByExe / ApplyMsiProps analysis that ran during
                // package creation. Read them here so Publish gets the right values.
                if (perApp != null)
                {
                    bool isExeTab = TabExe?.IsSelected == true;
                    if (isExeTab)
                    {
                        // EXE source. We do NOT special-case the MSI product code here:
                        // whether the MSI rule appears in Publish is governed entirely by
                        //   (a) the "MSI" rule-type checkbox  (_wantMsi), AND
                        //   (b) a non-empty product code in the JSON / selected-app data.
                        // Both are enforced in AutoPublishPage.BuildAndRenderDetectionRules,
                        // so a typed product code is honoured (MSI checked) and ignored
                        // (MSI unchecked) exactly as required. We still ensure the field is
                        // populated from the captured JSON value so it reaches Publish.
                        if (string.IsNullOrWhiteSpace(perApp.DetectionMsiProductCode) &&
                            !string.IsNullOrWhiteSpace(jsonFileMsiProductCode))
                            perApp.DetectionMsiProductCode = jsonFileMsiProductCode;
                        if (string.IsNullOrWhiteSpace(perApp.DetectionFileFolderPath))
                            perApp.DetectionFileFolderPath = "";
                        if (string.IsNullOrWhiteSpace(perApp.DetectionFileFileName))
                            perApp.DetectionFileFileName   = "";
                        if (string.IsNullOrWhiteSpace(perApp.DetectionFileValue))
                            perApp.DetectionFileValue      = "";
                        if (string.IsNullOrWhiteSpace(perApp.DetectionFileSizeValue))
                            perApp.DetectionFileSizeValue  = "";
                        if (string.IsNullOrWhiteSpace(perApp.DetectionRegKeyPath))
                            perApp.DetectionRegKeyPath     = "";
                        if (string.IsNullOrWhiteSpace(perApp.DetectionRegValueName))
                            perApp.DetectionRegValueName   = "";
                        if (string.IsNullOrWhiteSpace(perApp.DetectionRegistryValue))
                            perApp.DetectionRegistryValue  = "";
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(perApp.DetectionMsiProductCode))
                            perApp.DetectionMsiProductCode = "";
                        if (string.IsNullOrWhiteSpace(perApp.DetectionFileFolderPath))
                            perApp.DetectionFileFolderPath = "";
                        if (string.IsNullOrWhiteSpace(perApp.DetectionFileFileName))
                            perApp.DetectionFileFileName   = "";
                        if (string.IsNullOrWhiteSpace(perApp.DetectionFileValue))
                            perApp.DetectionFileValue      = "";
                        if (string.IsNullOrWhiteSpace(perApp.DetectionFileSizeValue))
                            perApp.DetectionFileSizeValue  = "";
                        if (string.IsNullOrWhiteSpace(perApp.DetectionRegKeyPath))
                            perApp.DetectionRegKeyPath     = "";
                        if (string.IsNullOrWhiteSpace(perApp.DetectionRegValueName))
                            perApp.DetectionRegValueName   = "";
                        if (string.IsNullOrWhiteSpace(perApp.DetectionRegistryValue))
                            perApp.DetectionRegistryValue  = "";
                    }
                }
                // ── End UI fallback ─────────────────────────────────────────────────────

                var s   = _cfg.IntuneSettings;
                var ov2 = _staticPendingIntuneOverrides;
                var sel = _staticSelectedApp;

                // Display name resolution (IntuneAppName/Vendor/Version)
                string displayName    = !string.IsNullOrWhiteSpace(_cfg.IntuneSettings.IntuneAppName)    ? _cfg.IntuneSettings.IntuneAppName    : appName;
                string displayVendor  = !string.IsNullOrWhiteSpace(_cfg.IntuneSettings.IntuneAppVendor)  ? _cfg.IntuneSettings.IntuneAppVendor  : appVendor;
                string displayVersion = !string.IsNullOrWhiteSpace(_cfg.IntuneSettings.IntuneAppVersion) ? _cfg.IntuneSettings.IntuneAppVersion : appVersion;

                // Dependency / Supersedence arrays
                var depNames = new[]
                {
                    perApp?.Dependency1 ?? "",
                    perApp?.Dependency2 ?? "",
                    perApp?.Dependency3 ?? "",
                    perApp?.Dependency4 ?? "",
                };
                var depAutoInstall = new[]
                {
                    _staticPendingAutoInstallDependency1,
                    _staticPendingAutoInstallDependency2,
                    _staticPendingAutoInstallDependency3,
                    _staticPendingAutoInstallDependency4,
                };
                var supNames = new[]
                {
                    perApp?.Supersedence1 ?? "",
                    perApp?.Supersedence2 ?? "",
                    perApp?.Supersedence3 ?? "",
                    perApp?.Supersedence4 ?? "",
                };
                var supUninstallPrev = new[]
                {
                    _staticPendingUninstallPreviousVersion1,
                    _staticPendingUninstallPreviousVersion2,
                    _staticPendingUninstallPreviousVersion3,
                    _staticPendingUninstallPreviousVersion4,
                };

                string deployType = !string.IsNullOrWhiteSpace(perApp?.DeploymentType) ? perApp!.DeploymentType : "";

                // Propagate detection-want flags
                _autoPublishPage.SetDetectionWants(_publishDetWantMsi, _publishDetWantFile, _publishDetWantReg);

                // Delegate all field population to AutoPublishPage — no PublishDeployPage involved
                _autoPublishPage.AutoFillFromPackage(
                    packageFolder:    packageFolder,
                    appName:          displayName,
                    appVendor:        displayVendor,
                    appVersion:       displayVersion,
                    description:      description,
                    perApp:           perApp,
                    ov2:              ov2,
                    settings:         s,
                    depNames:         depNames,
                    depAutoInstall:   depAutoInstall,
                    supNames:         supNames,
                    supUninstallPrev: supUninstallPrev,
                    deploymentType:   deployType,
                    sourceAppName:    _staticSelectedApp?.SourceAppName ?? "");  // actual installer name for registry lookup

                // Write package-specific Intune JSON (Intune\PackageName.json)
                // only when the Publish tab is actually being shown (not during pre-fill).
                // prefillOnly=true is used to capture registry detection data into memory
                // while the app is still installed — no JSON file is written at that point.
                if (!prefillOnly)
                    WriteIntuneAppSettingsJson(packageFolder);

                // Register Save callback — wires BtnSave to WriteIntuneAppSettingsJson
                if (_autoPublishPage != null)
                {
                    _autoPublishPage.OnSaveRequested = () =>
                    {
                        try { WriteIntuneAppSettingsJson(packageFolder, alwaysWrite: true); }
                        catch (Exception ex) { ShowValidationDialog("Auto Package - Save", $"Save failed: {ex.Message}", "⛔"); }
                    };
                }

                // ── prefillOnly: persist populated detection values back into _staticSelectedApp ──
                // AutoFillFromPackage populated p (a local perApp copy) with live registry data
                // (InstallLocation → DetectionFileFolderPath, main exe → DetectionFileFileName,
                // DisplayVersion → DetectionFileValue / DetectionRegistryValue, registry key path
                // → DetectionRegKeyPath). Without this step those values live only in the local
                // perApp and are discarded when DoNavigate() returns. The second NavigateToPublishTab
                // call (prefillOnly=false, after Intunewin) rebuilds perApp from the template JSON
                // and merges _staticSelectedApp — so we must write the populated values into
                // _staticSelectedApp now so the merge picks them up correctly.
                if (prefillOnly && perApp != null && _staticSelectedApp != null)
                {
                    static string NZ(string? a, string? b) => !string.IsNullOrWhiteSpace(a) ? a! : (b ?? "");
                    _staticSelectedApp.DetectionFileFolderPath  = NZ(perApp.DetectionFileFolderPath,  _staticSelectedApp.DetectionFileFolderPath);
                    _staticSelectedApp.DetectionFileFileName    = NZ(perApp.DetectionFileFileName,    _staticSelectedApp.DetectionFileFileName);
                    _staticSelectedApp.DetectionFileValue       = NZ(perApp.DetectionFileValue,       _staticSelectedApp.DetectionFileValue);
                    _staticSelectedApp.DetectionFileSizeValue   = NZ(perApp.DetectionFileSizeValue,   _staticSelectedApp.DetectionFileSizeValue);
                    // Date created / Date modified: persist the auto-fetched date+time too, so they
                    // survive the source .exe being uninstalled before Publish Save runs (the exe is
                    // gone by then and cannot be re-read). Format: MM/DD/YYYY and HH:MM:SS AM/PM.
                    _staticSelectedApp.DetectionFileDateValue   = NZ(perApp.DetectionFileDateValue,   _staticSelectedApp.DetectionFileDateValue);
                    _staticSelectedApp.DetectionFileTimeValue   = NZ(perApp.DetectionFileTimeValue,   _staticSelectedApp.DetectionFileTimeValue);
                    _staticSelectedApp.DetectionRegKeyPath      = NZ(perApp.DetectionRegKeyPath,      _staticSelectedApp.DetectionRegKeyPath);
                    _staticSelectedApp.DetectionRegValueName    = NZ(perApp.DetectionRegValueName,    _staticSelectedApp.DetectionRegValueName);
                    _staticSelectedApp.DetectionRegistryValue   = NZ(perApp.DetectionRegistryValue,   _staticSelectedApp.DetectionRegistryValue);
                    _staticSelectedApp.DetectionMsiProductCode  = NZ(perApp.DetectionMsiProductCode,  _staticSelectedApp.DetectionMsiProductCode);
                    _staticSelectedApp.DetectionMsiValue        = NZ(perApp.DetectionMsiValue,        _staticSelectedApp.DetectionMsiValue);
                }
                else if (prefillOnly && perApp != null && _staticSelectedApp == null)
                {
                    // No _staticSelectedApp yet — promote the populated perApp directly so
                    // the second call's merge path (line 1231) picks it up as _staticSelectedApp.
                    _staticSelectedApp = perApp;
                }

                // When called for pre-fill only (between Install and Uninstall), stop here.
                // The tab will be activated and Save triggered by the real publishAction call
                // that fires after Intunewin completes.
                if (prefillOnly) return;

                ActivateTopTab(activeTab: "Publish");

                // Auto-trigger Save (PublishSettings=Yes or PublishToIntune=Yes)
                // Capture whether Save will run so the Publish delay can account for it.
                bool saveTriggered = _autoTriggerSave && _autoPublishPage != null;
                if (saveTriggered)
                {
                    _autoTriggerSave = false;
                    var pg = _autoPublishPage;
                    try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] TriggerSave - PublishSettings=Yes{Environment.NewLine}"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
                        Dispatcher.Invoke(() =>
                        {
                            pg.TriggerSave();
                            // Show a "Publish — Complete" status dialog when ShowMessageBox = Yes,
                            // BUT only when Publish-to-Intune is NOT going to run. When it will,
                            // OnPublishComplete shows the dialog after the publish script finishes.
                            if (AutoShowMessageBox.Value && !_publishToIntuneIsTerminus)
                            {
                                try
                                {
                                    AutoStatusDialog.Show(this, "Publish - Complete", "🚀", true,
                                        new System.Collections.Generic.List<(string,string,bool)>
                                        {
                                            ("Time", $"Finished: {DateTime.Now:HH:mm:ss}", false),
                                            ("App",  pg.AppName ?? "", false),
                                            ("Status", "Publish settings saved", true),
                                        });
                                }
                                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                            }
                            // Publish Save is the terminal step ONLY when no publish-to-Intune
                            // follows. When PublishToIntune=Yes, the publish-script completion
                            // (OnPublishComplete) raises all-complete instead.
                            if (!_publishToIntuneIsTerminus)
                                RaiseAutoFlowComplete();
                        }));
                }
                else
                {
                    // Publish tab opened but no auto-save configured. Terminus here only
                    // when no publish-to-Intune step will follow.
                    if (!_publishToIntuneIsTerminus)
                        RaiseAutoFlowComplete();
                }

                // Fire deferred Uninstall — NavigateToPublishTab just opens the tab.
                var pendingUninstall = _pendingAfterPublishUninstallExecute;
                _pendingAfterPublishUninstallExecute = null;
                if (pendingUninstall != null)
                {
                    try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] Firing deferred UninstallPackage after Publish tab opened{Environment.NewLine}"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    System.Threading.Tasks.Task.Delay(800).ContinueWith(_ =>
                        Dispatcher.Invoke(pendingUninstall));
                }
            }

            if (Dispatcher.CheckAccess())
                DoNavigate();
            else
                Dispatcher.Invoke(DoNavigate);
        }

        public void ActivateTopTab(string activeTab)
        {
            bool showPackage   = activeTab == "Package";
            bool showExecute   = activeTab == "Execute";
            bool showDownload  = activeTab == "Download";
            bool showIntunewin = activeTab == "Intunewin";
            bool showPublish   = activeTab == "Publish";
            bool showDocument  = activeTab == "Document";

            // Content visibility
            if (FrameDownload        != null) FrameDownload.Visibility        = showDownload  ? Visibility.Visible : Visibility.Collapsed;
            if (PkgContentCard       != null) PkgContentCard.Visibility       = showPackage   ? Visibility.Visible : Visibility.Collapsed;
            if (PkgToolbarCard       != null) PkgToolbarCard.Visibility       = showPackage   ? Visibility.Visible : Visibility.Collapsed;
            if (FrameExecute         != null) FrameExecute.Visibility         = showExecute   ? Visibility.Visible : Visibility.Collapsed;
            if (FrameIntunewin != null) FrameIntunewin.Visibility = showIntunewin ? Visibility.Visible : Visibility.Collapsed;
            if (FramePublish         != null) FramePublish.Visibility         = showPublish   ? Visibility.Visible : Visibility.Collapsed;
            if (FrameDocument        != null) FrameDocument.Visibility        = showDocument  ? Visibility.Visible : Visibility.Collapsed;

            // Lazily load AutoExecutePage on first Execute tab activation
            if (showExecute) EnsureAutoExecPageLoaded();

            // Lazily load AutoPublishPage on first Publish tab activation
            if (showPublish) EnsureAutoPublishPageLoaded();

            // Lazily load AutoDocumentPage on first Document tab activation, then refresh it.
            if (showDocument) { EnsureAutoDocumentPageLoaded(); RefreshDocumentTab(); }

            var accentBrush  = TryFindResource("AccentBlueBrush")   as System.Windows.Media.Brush
                               ?? System.Windows.Media.Brushes.CornflowerBlue;
            var clearBrush   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent);
            var activeTextBrush = System.Windows.Media.Brushes.White as System.Windows.Media.Brush; // active-tab text on the accent fill
            var mutedBrush   = TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush
                               ?? System.Windows.Media.Brushes.Gray;

            // Active tab is highlighted with a filled (accent) rounded background; inactive tabs clear.
            if (TabDownloadIndicator  != null)
                TabDownloadIndicator.Background  = showDownload  ? accentBrush : clearBrush;
            if (TabPackageIndicator   != null)
                TabPackageIndicator.Background   = showPackage   ? accentBrush : clearBrush;
            if (TabExecuteIndicator   != null)
                TabExecuteIndicator.Background   = showExecute   ? accentBrush : clearBrush;
            if (TabIntunewinIndicator != null)
                TabIntunewinIndicator.Background = showIntunewin ? accentBrush : clearBrush;
            if (TabPublishIndicator   != null)
                TabPublishIndicator.Background   = showPublish   ? accentBrush : clearBrush;
            if (TabDocumentIndicator  != null)
                TabDocumentIndicator.Background  = showDocument  ? accentBrush : clearBrush;

            void SetTabColors(Button btn, bool isActive)
            {
                if (btn?.Content is not System.Windows.Controls.StackPanel sp) return;
                foreach (var tb in sp.Children.OfType<TextBlock>())
                    tb.Foreground = isActive ? activeTextBrush : mutedBrush;
            }
            SetTabColors(BtnTabDownload,  showDownload);
            SetTabColors(BtnTabPackage,   showPackage);
            if (BtnTabExecute   != null) SetTabColors(BtnTabExecute,   showExecute);
            if (BtnTabIntunewin != null) SetTabColors(BtnTabIntunewin, showIntunewin);
            if (BtnTabPublish   != null) SetTabColors(BtnTabPublish,   showPublish);
            if (BtnTabDocument  != null) SetTabColors(BtnTabDocument,  showDocument);
        }

        // ══════════════════════════════════════════════════════════════════
        //  LOADED — read config, pre-populate defaults
        // ══════════════════════════════════════════════════════════════════
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Always re-wire clicks (handlers must be re-attached on each navigation)
            BtnCreate_Ctrl.Click     -= BtnCreate_Click;
            BtnCreate_Ctrl.Click     += BtnCreate_Click;
            BtnClearAll_Ctrl.Click   -= BtnClearAll_Click;
            BtnClearAll_Ctrl.Click   += BtnClearAll_Click;
            BtnRefresh_Ctrl.Click    -= BtnRefresh_Click;
            BtnRefresh_Ctrl.Click    += BtnRefresh_Click;
            BtnCancel_Ctrl.Click     -= BtnCancel_Click;
            BtnCancel_Ctrl.Click     += BtnCancel_Click;

            ApplyPackageViewLock();

            // Open Log / Open Folder visibility — only reset on first load
            if (!_initialized)
            {
                MainTabControl.SelectedIndex = 0;
                // Load config and apply defaults only on first visit —
                // subsequent navigations preserve all user-entered data.
                LoadConfig();
                ApplyConfigDefaults();

                // Navigate FrameDownload to SourceDownloadPage via reflection — no compile-time dependency
                if (FrameDownload?.Content == null)
                {
                    try
                    {
                        var sdpType = System.Type.GetType("SwiftPack.Views.SourceDownloadPage") ??
                            AppDomain.CurrentDomain.GetAssemblies()
                                .SelectMany(a => { try { return a.GetTypes(); } catch { return System.Array.Empty<Type>(); } })
                                .FirstOrDefault(t => t.Name == "SourceDownloadPage");
                        if (sdpType != null)
                        {
                            var sdpInstance = Activator.CreateInstance(sdpType);
                            FrameDownload?.Navigate(sdpInstance);
                        }
                    }
                    catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }

                _initialized = true;
            }

            // Default to Download tab on every load
            ActivateTopTab(activeTab: "Download");

            // Pre-load AutoIntunewinPage into FrameIntunewin on startup
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(EnsureIntunewinPageLoaded));

            // Smart caret + deselect + context menu on all TextBoxes
            AttachCaretHandlerToAllTextBoxes(this);
            this.PreviewMouseLeftButtonDown -= Page_PreviewMouseLeftButtonDown;
            this.PreviewMouseLeftButtonDown += Page_PreviewMouseLeftButtonDown;

            // The TabControl uses ContentPresenter ContentSource=SelectedContent,
            // so only the ACTIVE tab's visual tree exists at any time.
            // Re-attach caret handlers every time the tab switches so EXE-tab
            // textboxes (Uninstall Source, Template Folder, detection value, etc.)
            // get the same smart-caret behaviour as MSI-tab textboxes.
            MainTabControl.SelectionChanged -= MainTabControl_SelectionChanged;
            MainTabControl.SelectionChanged += MainTabControl_SelectionChanged;

            // Always re-run visibility pass (controls may not be ready yet)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(UpdatePanelVisibility));

            // Initial deferred attach for the first rendered tab
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => AttachCaretHandlerToAllTextBoxes(this)));
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Re-attach after the new tab's content has been laid out.
            // Use Background priority so the ContentPresenter has swapped its content.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => AttachCaretHandlerToAllTextBoxes(this)));
        }

        private void Page_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Deselect any focused TextBox when the user clicks somewhere else on the page.
            // "Somewhere else" means: a different element, or the empty area of the same textbox
            // (the PathBox handler takes over for in-box empty-area clicks).
            if (System.Windows.Input.Keyboard.FocusedElement is not TextBox focused) return;
            if (focused.SelectionLength == 0) return;

            // If the click landed on a different element entirely — clear selection
            var hit = e.OriginalSource as System.Windows.DependencyObject;
            bool clickedSameBox = false;
            var el = hit;
            while (el != null)
            {
                if (ReferenceEquals(el, focused)) { clickedSameBox = true; break; }
                el = System.Windows.Media.VisualTreeHelper.GetParent(el);
            }
            if (!clickedSameBox) focused.SelectionLength = 0;
        }

        private void AttachCaretHandlerToAllTextBoxes(System.Windows.DependencyObject parent)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is TextBox tb)
                {
                    // Remove first to avoid double-attach on re-navigation
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
            if (sender is not TextBox tb) return;

            // ── Requirement: click anywhere in the page deselects a selection ──────
            // If this textbox already has a selection AND the click landed in the
            // empty area beyond the text, first clear the selection and position the
            // caret, then start a potential drag — handled below.

            var clickPoint = e.GetPosition(tb);
            int exactIndex = tb.GetCharacterIndexFromPoint(clickPoint, snapToText: false);

            // Click landed directly on a character — let WPF handle normally,
            // EXCEPT if there is a current selection we want to clear it first so
            // a single click always deselects (matches standard editor behaviour).
            if (exactIndex != -1)
            {
                if (tb.SelectionLength > 0)
                {
                    // Clear selection; position caret at clicked character.
                    // Let WPF continue (don't set e.Handled) so the click also
                    // focuses and positions naturally.
                    tb.Select(exactIndex, 0);
                }
                return;  // normal WPF handling for on-text clicks
            }

            // Click landed in empty area beyond the text ───────────────────────────
            if (string.IsNullOrEmpty(tb.Text))
            {
                if (!tb.IsFocused) tb.Focus();
                return;
            }

            int nearestIndex = tb.GetCharacterIndexFromPoint(clickPoint, snapToText: true);
            // Determine which end of the text is closer to decide the caret anchor.
            // We use the pixel position of the first and last characters for accuracy.
            var firstRect = tb.GetRectFromCharacterIndex(0);
            var lastRect  = tb.GetRectFromCharacterIndex(tb.Text.Length);
            bool goToStart = Math.Abs(clickPoint.X - firstRect.Left)
                           < Math.Abs(clickPoint.X - lastRect.Right);

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

                // Clamp to text boundaries
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

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var opts = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    };
                    _cfg = JsonSerializer.Deserialize<AutoPackageConfig>(json, opts) ?? new();
                }
            }
            catch (System.Exception ex)
            {
                // One bad field must not silently blank every setting again: keep the
                // reset (the page cannot run without a config object) but leave a trace.
                System.Diagnostics.Debug.WriteLine("[AutoPackagePage] LoadConfig failed - config reset to defaults: " + ex.Message);
                _cfg = new();
            }
        }

        private void ApplyConfigDefaults()
        {
            // Template folder — apply to both MSI tab and EXE tab
            if (!string.IsNullOrWhiteSpace(_cfg.TemplateFolderPath))
            {
                TxtTemplateFolder.Text = _cfg.TemplateFolderPath;
                if (ExeTxtTemplateFolder != null)
                    ExeTxtTemplateFolder.Text = _cfg.TemplateFolderPath;
            }

            // Apply all dropdowns silently — always set from JSON so user's saved
            // preferences are respected. XAML defaults are all No; JSON drives actual value.
            _suppressVisibilityUpdate = true;
            SetCombo(CmbCreateMst,       _cfg.CreateMST);
            SetCombo(CmbRemoveDesktop,   _cfg.RemoveDesktopShortcut);
            // Install/Uninstall Package — only apply (and keep visible) when template folder is set
            if (!string.IsNullOrWhiteSpace(_cfg.TemplateFolderPath))
            {
                if (FindName("CmbCreateDocument") is ComboBox cmbCD0) SetCombo(cmbCD0, _cfg.CreateDocument);
                // CreateDocument combo (added via XAML when control exists)
                if (FindName("CmbCreateDocument") is ComboBox cmbCD) SetCombo(cmbCD, _cfg.CreateDocument);
                // InstallBehaviour dropdown
            }
            _suppressVisibilityUpdate = false;

            // Wire RadioButton Checked events for converted controls
            void WireRb(StackPanel? sp, RoutedEventHandler h)
            {
                if (sp == null) return;
                foreach (var rb in sp.Children.OfType<RadioButton>()) rb.Checked += h;
            }
            WireRb(CmbCreateMst,       CmbCreateMst_Changed);
            // Note: if AutoPackage.json doesn't exist, _cfg fields default to false = No

            // Group name — prefer DeployGroupName, fall back to IntuneSettings.Assignments.Available
            string groupName = !string.IsNullOrWhiteSpace(_cfg.DeployGroupName)
                ? _cfg.DeployGroupName
                : _cfg.IntuneSettings.AssignmentsAvailable;
            if (!string.IsNullOrWhiteSpace(groupName))
            {
            }

            // App Vendor (package/app vendor — NOT the Intune "Developer" field).
            string publisher = !string.IsNullOrWhiteSpace(_cfg.IntuneSettings.IntuneAppVendor)
                ? _cfg.IntuneSettings.IntuneAppVendor
                : _cfg.IntuneSettings.Publisher;
            if (!string.IsNullOrWhiteSpace(publisher))
                TxtPublisher.Text = publisher;
            if (!string.IsNullOrWhiteSpace(_cfg.IntuneSettings.Description))
            {
            }

            // Install / Uninstall commands — only show when template folder is already populated.
            // If no template folder, the rows stay hidden; they appear when the user browses one.
            bool templateAlreadySet = !string.IsNullOrWhiteSpace(_cfg.TemplateFolderPath);
            if (templateAlreadySet)
            {
                if (!string.IsNullOrWhiteSpace(_cfg.IntuneSettings.InstallCommand))
                {
                    if (TxtInstallCmd    != null) TxtInstallCmd.Text    = _cfg.IntuneSettings.InstallCommand;
                    if (ExeTxtInstallCmd != null) ExeTxtInstallCmd.Text = _cfg.IntuneSettings.InstallCommand;
                }
                if (!string.IsNullOrWhiteSpace(_cfg.IntuneSettings.UninstallCommand))
                {
                    if (TxtUninstallCmd    != null) TxtUninstallCmd.Text    = _cfg.IntuneSettings.UninstallCommand;
                    if (ExeTxtUninstallCmd != null) ExeTxtUninstallCmd.Text = _cfg.IntuneSettings.UninstallCommand;
                }
                UpdateCommandRowVisibility(true);
                UpdateExeCommandRowVisibility(true);
            }

            // Detection method + operator defaults from AutoPackage.json IntuneSettings
            // Applied to both MSI tab and EXE tab combos
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                // MSI tab
                // EXE tab
            }));

            // Single visibility pass now that all values are set
            UpdatePanelVisibility();
        }

        // ── Helper: set combo by string content (case-insensitive) ─────────────────
        private static void SetComboByContent(ComboBox? cmb, string value)
        {
            if (cmb == null || string.IsNullOrWhiteSpace(value)) return;
            foreach (ComboBoxItem item in cmb.Items)
                if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                { cmb.SelectedItem = item; return; }
        }

        // ── StackPanel (RadioButton) overloads ────────────────────────────
        private static void SetCombo(StackPanel? sp, bool value)
            => SetComboByContent(sp, value ? "Yes" : "No");

        private static void SetComboByContent(StackPanel? sp, string value)
        {
            if (sp == null || string.IsNullOrWhiteSpace(value)) return;
            foreach (var rb in sp.Children.OfType<RadioButton>())
                rb.IsChecked = string.Equals(rb.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ComboIsYes(StackPanel? sp)
            => sp?.Children.OfType<RadioButton>()
                  .Any(rb => rb.IsChecked == true &&
                       !string.Equals(rb.Content?.ToString(), "No", StringComparison.OrdinalIgnoreCase)) == true;

        // Returns the selected value string ("No", "Administrator", or "System Context")
        private static string ComboValue(StackPanel? sp)
            => sp?.Children.OfType<RadioButton>()
                  .FirstOrDefault(rb => rb.IsChecked == true)
                  ?.Content?.ToString() ?? "No";

        private static void SetCombo(FrameworkElement? ctrl, bool value)
        {
            if (ctrl is ComboBox cb) SetCombo(cb, value);
            else if (ctrl is StackPanel sp) SetCombo(sp, value);
        }

        private static bool ComboIsYes(FrameworkElement? ctrl)
        {
            if (ctrl is ComboBox cb) return ComboIsYes(cb);
            if (ctrl is StackPanel sp) return ComboIsYes(sp);
            return false;
        }

        // ── Helper: set Yes/No combo — searches by content so item order doesn't matter ──
        private static void SetCombo(ComboBox cmb, bool value)
        {
            string target = value ? "Yes" : "No";
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                if ((cmb.Items[i] as ComboBoxItem)?.Content?.ToString() == target)
                {
                    cmb.SelectedIndex = i;
                    return;
                }
            }
            cmb.SelectedIndex = 0; // fallback
        }

        private static bool ComboIsYes(ComboBox? cmb)
            => cmb != null && (cmb.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Yes";

        // ══════════════════════════════════════════════════════════════════
        //  PANEL VISIBILITY LOGIC
        // ══════════════════════════════════════════════════════════════════
        private void UpdatePanelVisibility()
        {
            if (_suppressVisibilityUpdate ||
                PanelDeploySection == null) return;

            // Close Applications — always visible; enabled only when template folder has Invoke script
            bool hasInvoke = HasInvokeScript();
            if (TxtCloseApps != null)
            {
                TxtCloseApps.IsEnabled = hasInvoke;
                TxtCloseApps.Opacity   = hasInvoke ? 1.0 : 0.45;
                TxtCloseApps.Tag       = "";
            }

            // MSI ArgumentList + Additional ArgumentList — disabled when no template folder
            bool hasTemplate = !string.IsNullOrWhiteSpace(TxtTemplateFolder?.Text?.Trim());
            if (TxtMsiArgumentList != null)
            {
                TxtMsiArgumentList.IsEnabled = hasTemplate;
                TxtMsiArgumentList.Opacity   = hasTemplate ? 1.0 : 0.45;
            }
            if (TxtAdditionalArgs != null)
            {
                TxtAdditionalArgs.IsEnabled = hasTemplate;
                TxtAdditionalArgs.Opacity   = hasTemplate ? 1.0 : 0.45;
            }

            // All Intune rows always visible — no conditional hiding
        }

        // ── Check for Invoke-AppDeployToolkit.ps1 in template folder ──────
        private bool HasInvokeScript()
        {
            string tpl = TxtTemplateFolder.Text.Trim();
            if (string.IsNullOrWhiteSpace(tpl) || !Directory.Exists(tpl))
                return false;
            return Directory.GetFiles(tpl, "Invoke-AppDeployToolkit.ps1",
                       SearchOption.AllDirectories).Any();
        }

        // ══════════════════════════════════════════════════════════════════
        //  BROWSE HANDLERS
        // ══════════════════════════════════════════════════════════════════
        // ── Legacy per-tab browse handlers (kept for any remaining XAML references) ────
        private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
            => HdrBtnBrowseFile_Click(sender, e);

        private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
            => HdrBtnBrowseFolder_Click(sender, e);

        // ── Header browse handlers — detect MSI vs EXE from extension ──────────────
        private void HdrBtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title            = "Select Install Source",
                Filter           = "All Install Files (*.msi;*.exe;*.ps1;*.bat;*.cmd)|*.msi;*.exe;*.ps1;*.bat;*.cmd|All Files (*.*)|*.*",
                InitialDirectory = @"C:\"
            };
            if (dlg.ShowDialog() != true) return;
            LoadSourceFromHeader(dlg.FileName);
        }

        private void HdrBtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(
                Window.GetWindow(this)!).Handle;
            string? picked = ShellFolderPicker.Browse(hwnd,
                "Select Source Folder", @"C:\");
            if (picked != null) LoadSourceFromHeader(picked);
        }

        private void HdrBtnCancelSource_Click(object sender, RoutedEventArgs e)
        {
            HdrTxtSource.TextChanged -= HdrTxtSource_TextChanged;
            HdrTxtSource.Clear();
            HdrTxtSource.TextChanged += HdrTxtSource_TextChanged;
            // Also sync the hidden TxtSource for MSI path tracking
            _msiPath = "";
            TxtSource.TextChanged -= TxtSource_TextChanged;
            TxtSource.Clear();
            TxtSource.TextChanged += TxtSource_TextChanged;
            ClearMsiFields();
            ClearMsiDetectionFields();
            ExeTxtSource?.Clear();
        }

        private void HdrTxtSource_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files?.Length > 0) LoadSourceFromHeader(files[0]);
            e.Handled = true;
        }

        private void HdrTxtSource_TextChanged(object sender, TextChangedEventArgs e)
        {
            string src = HdrTxtSource.Text.Trim();
            if (string.IsNullOrWhiteSpace(src)) return;
            // Only act when the path exists (avoid processing partial typed paths)
            if (!File.Exists(src) && !Directory.Exists(src)) return;
            LoadSourceFromHeader(src);
        }

        /// <summary>
        /// Central entry point for all header source changes.
        /// Detects extension - switches tab - delegates to MSI or EXE loader.
        /// Non-executable files (e.g. .ttf, .docx) clear all fields.
        /// </summary>
        // ── Called by MainWindow after "Set as Source Path" in Source Download ──
        /// <summary>Pre-fills the Source Path field from an externally downloaded file.</summary>
        public void SetSourcePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            Dispatcher.Invoke(() => LoadSourceFromHeader(filePath));
        }

        /// <summary>
        /// Pre-fills App Vendor, App Name and Close Applications fields from
        /// SourceDownload.json values (AppVendor, AppName, CloseApps).
        /// Only sets each field if the provided value is non-empty, so
        /// auto-detected values are not overwritten with blanks.
        /// Stored as pending values — applied in TriggerAutoCreate after
        /// _sourceFullyLoaded, so the EXE/MSI pipeline cannot clear them.
        /// </summary>
        public void SetPackageMetadata(string appVendor = "", string appName = "", string closeApps = "",
            string appVersion = "", string uninstallPath = "",
            string createIntunewin = "", string publishToIntune = "", string assignToGroup   = "",
            string msiInstallArgs = "", string msiUninstallArgs = "",
            string deploymentType = "", string detectionType = "", string description = "",
            string groupName = "")
        {
            if (!string.IsNullOrWhiteSpace(appVendor))         _pendingAppVendor        = appVendor;
            if (!string.IsNullOrWhiteSpace(appName))           _pendingAppName          = appName;
            if (!string.IsNullOrWhiteSpace(closeApps))         _pendingCloseApps        = closeApps;
            if (!string.IsNullOrWhiteSpace(appVersion))        _pendingAppVersion       = appVersion;
            if (!string.IsNullOrWhiteSpace(uninstallPath))     _pendingUninstallPath    = uninstallPath;
            if (!string.IsNullOrWhiteSpace(createIntunewin))   _pendingCreateIntunewin  = createIntunewin;
            if (!string.IsNullOrWhiteSpace(publishToIntune))   _pendingPublishToIntune  = publishToIntune;
            if (!string.IsNullOrWhiteSpace(assignToGroup))     _pendingAssignToGroup    = assignToGroup;
            if (!string.IsNullOrWhiteSpace(msiInstallArgs))    _pendingMsiInstallArgs   = msiInstallArgs;
            if (!string.IsNullOrWhiteSpace(msiUninstallArgs))  _pendingMsiUninstallArgs = msiUninstallArgs;
            if (!string.IsNullOrWhiteSpace(deploymentType))    _pendingDeploymentType   = deploymentType;
            if (!string.IsNullOrWhiteSpace(detectionType))     _pendingDetectionType    = detectionType;
            if (!string.IsNullOrWhiteSpace(description))       _pendingDescription      = description;
            if (!string.IsNullOrWhiteSpace(groupName))         _pendingGroupName         = groupName;
        }

        /// <summary>
        /// Sets the PackageTemplateFolder to apply after package folder creation.
        /// Copies Invoke-AppDeployToolkit.ps1 from the template to the package Native folder,
        /// and copies all Files\* content from the template to the package Native\Files folder.
        /// </summary>
        public void SetPackageTemplateFolder(string templateFolder)
        {
            _pendingTemplateFolder = templateFolder ?? "";
        }

        /// <summary>
        /// Sets both the Template Folder UI field and the pending template folder for file copying.
        /// Call this when triggering from Source Download with a PSADTTemplatePath.
        /// </summary>
        public void SetTemplateFolderUI(string templateFolder)
        {
            _pendingTemplateFolder   = templateFolder ?? "";
            _pendingTemplateFolderUI = templateFolder ?? "";
            // Set the UI field immediately on the UI thread
            Dispatcher.Invoke(() =>
            {
                if (TxtTemplateFolder    != null) TxtTemplateFolder.Text    = templateFolder ?? "";
                if (ExeTxtTemplateFolder != null) ExeTxtTemplateFolder.Text = templateFolder ?? "";
            });
        }

        /// <summary>
        /// Copies template files into the created package folder:
        /// - Template\Invoke-AppDeployToolkit.ps1 → packageNativeFolder\Invoke-AppDeployToolkit.ps1
        /// - Template\Files\* → packageNativeFolder\Files\* (all files/folders recursively)
        /// </summary>
        private static void ApplyPackageTemplate(string templateFolder, string packageNativeFolder)
        {
            if (string.IsNullOrWhiteSpace(templateFolder) ||
                !System.IO.Directory.Exists(templateFolder)) return;
            if (string.IsNullOrWhiteSpace(packageNativeFolder)) return;

            try
            {
                // Copy Invoke-AppDeployToolkit.ps1 from template root to package native folder
                string srcPs1  = System.IO.Path.Combine(templateFolder, "Invoke-AppDeployToolkit.ps1");
                string destPs1 = System.IO.Path.Combine(packageNativeFolder, "Invoke-AppDeployToolkit.ps1");
                if (System.IO.File.Exists(srcPs1))
                    System.IO.File.Copy(srcPs1, destPs1, overwrite: true);

                // Copy template Files\* → package Native\Files\
                string srcFiles  = System.IO.Path.Combine(templateFolder, "Files");
                string destFiles = System.IO.Path.Combine(packageNativeFolder, "Files");
                if (System.IO.Directory.Exists(srcFiles))
                    CopyDirectoryRecursive(srcFiles, destFiles);
            }
            catch { /* silently ignore template copy errors */ }
        }

        private static void CopyDirectoryRecursive(string src, string dest)
        {
            System.IO.Directory.CreateDirectory(dest);
            foreach (var file in System.IO.Directory.GetFiles(src))
            {
                string destFile = System.IO.Path.Combine(dest, System.IO.Path.GetFileName(file));
                System.IO.File.Copy(file, destFile, overwrite: true);
            }
            foreach (var dir in System.IO.Directory.GetDirectories(src))
            {
                string destDir = System.IO.Path.Combine(dest, System.IO.Path.GetFileName(dir));
                CopyDirectoryRecursive(dir, destDir);
            }
        }

        /// <summary>
        /// Sets a callback to fire after the Auto Package Complete dialog is closed.
        /// Used to trigger Silent Uninstall only after Create Package fully completes.
        /// </summary>
        public void SetAfterCreateCallback(Action? callback)
        {
            _onAfterCreateComplete = callback;
        }

        /// <summary>Fired when the auto-package flow successfully creates a .intunewin file.</summary>
        public Action<string>? OnIntunewinCreated { get; set; }
        public void SetInstallUninstallArgs(string installArgs, string uninstallArgs)
        {
            // Store for use in TriggerAutoCreate — must be applied AFTER source is fully
            // loaded to avoid being cleared by the ExeTxtSource_TextChanged pipeline.
            _pendingInstallArgs   = installArgs;
            _pendingUninstallArgs = uninstallArgs;
            // Also persist install args for ExeRebuildCommands which may fire before
            // TriggerAutoCreate applies _pendingInstallArgs to ExeTxtInstallArgs.
            if (!string.IsNullOrWhiteSpace(installArgs))
                _jsonInstallArgs = installArgs;
        }

        /// <summary>Returns true when source has been fully processed (all props applied).</summary>
        public bool IsSourceLoaded => _sourceFullyLoaded;

        /// <summary>Waits until source is fully loaded then triggers the Create button.</summary>
        public async Task TriggerAutoCreate()
        {
            // Mark that this Create was initiated from Source Download (Create Package checked).
            _triggeredFromSourceDownload = true;

            // Switch to Package tab so the user sees the creation progress
            SwitchToPackageTab();

            // Give a short initial delay for SetTemplateFolderUI/SetPackageMetadata to be called
            // from SourceDownloadPage.BeginInvoke before the poll loop starts checking _sourceFullyLoaded
            await Task.Delay(200);

            // Poll up to 15 seconds in 300ms intervals until source props are fully applied
            int waited = 200;
            while (!_sourceFullyLoaded && waited < 15000)
            {
                await Task.Delay(300);
                waited += 300;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (!_sourceFullyLoaded) return; // timed out - do not fire

                // Absorb static pending values (set before navigation from SourceDownloadPage)
                if (!string.IsNullOrWhiteSpace(_staticPendingTemplateFolder))   { _pendingTemplateFolderUI   = _staticPendingTemplateFolder;   _staticPendingTemplateFolder   = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingCreateIntunewin))  { _pendingCreateIntunewin    = _staticPendingCreateIntunewin;   _staticPendingCreateIntunewin  = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingPublishToIntune))  { _pendingPublishToIntune    = _staticPendingPublishToIntune;   _staticPendingPublishToIntune  = ""; }
                // Carry PublishToIntune flag into NavigateToPublishTab
                if (!string.IsNullOrWhiteSpace(_pendingPublishToIntune))
                    _pendingAutoPublishAfterUninstall = _pendingPublishToIntune;
                if (!string.IsNullOrWhiteSpace(_staticPendingAssignToGroup))    { _pendingAssignToGroup      = _staticPendingAssignToGroup;     _staticPendingAssignToGroup    = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingMsiInstallArgs))   { _pendingMsiInstallArgs     = _staticPendingMsiInstallArgs;    _staticPendingMsiInstallArgs   = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingMsiUninstallArgs)) { _pendingMsiUninstallArgs   = _staticPendingMsiUninstallArgs;  _staticPendingMsiUninstallArgs = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingDeploymentType))   { _pendingDeploymentType     = _staticPendingDeploymentType;    _staticPendingDeploymentType   = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingDetectionType))    { _pendingDetectionType      = _staticPendingDetectionType;     _staticPendingDetectionType    = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingDetTypeMSI))       { _pendingDetTypeMSI         = _staticPendingDetTypeMSI;         _staticPendingDetTypeMSI       = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingDetTypeFile))      { _pendingDetTypeFile        = _staticPendingDetTypeFile;        _staticPendingDetTypeFile      = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingDetTypeRegistry))  { _pendingDetTypeRegistry    = _staticPendingDetTypeRegistry;    _staticPendingDetTypeRegistry  = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingDetTypeScript))    { _pendingDetTypeScript      = _staticPendingDetTypeScript;      _staticPendingDetTypeScript    = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingRulesFormat))      { _pendingRulesFormat        = _staticPendingRulesFormat;        _staticPendingRulesFormat      = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingDescription))      { _pendingDescription        = _staticPendingDescription;       _staticPendingDescription      = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingGroupName))              { _pendingGroupName          = _staticPendingGroupName;              _staticPendingGroupName              = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingDependency1))    { _pendingDependency1    = _staticPendingDependency1;    _staticPendingDependency1    = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingDependency2))    { _pendingDependency2    = _staticPendingDependency2;    _staticPendingDependency2    = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingDependency3))    { _pendingDependency3    = _staticPendingDependency3;    _staticPendingDependency3    = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingDependency4))    { _pendingDependency4    = _staticPendingDependency4;    _staticPendingDependency4    = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingSupersedence1))  { _pendingSupersedence1  = _staticPendingSupersedence1;  _staticPendingSupersedence1  = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingSupersedence2))  { _pendingSupersedence2  = _staticPendingSupersedence2;  _staticPendingSupersedence2  = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingSupersedence3))  { _pendingSupersedence3  = _staticPendingSupersedence3;  _staticPendingSupersedence3  = ""; }
                if (!string.IsNullOrWhiteSpace(_staticPendingSupersedence4))  { _pendingSupersedence4  = _staticPendingSupersedence4;  _staticPendingSupersedence4  = ""; }
                // Apply PackageOutputFolder, CreateMST, RemoveDesktopShortcut from per-app JSON
                if (!string.IsNullOrWhiteSpace(_staticPendingPackageOutputFolder))
                {
                    // Override the config output folder for this run only
                    _cfg.PackageOutputFolder = _staticPendingPackageOutputFolder;
                    _staticPendingPackageOutputFolder = "";
                }
                if (!string.IsNullOrWhiteSpace(_staticPendingCreateMst))
                {
                    bool v = _staticPendingCreateMst.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    if (CmbCreateMst != null) SetCombo(CmbCreateMst, v);
                    _staticPendingCreateMst = "";
                }
                if (!string.IsNullOrWhiteSpace(_staticPendingRemoveDesktopShortcut))
                {
                    bool v = _staticPendingRemoveDesktopShortcut.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    if (CmbRemoveDesktop != null) SetCombo(CmbRemoveDesktop, v);
                    _staticPendingRemoveDesktopShortcut = "";
                }
                // Apply InstallPackage / UninstallPackage from Source Download checkboxes
                if (!string.IsNullOrWhiteSpace(_staticPendingInstallPackage))
                {
                    // "Yes" from checkbox → treat as "Administrator" (PSADT default context)
                    if (_cfg != null)
                        _cfg.InstallPackage = _staticPendingInstallPackage.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                            ? "Administrator"
                            : _staticPendingInstallPackage;
                    _staticPendingInstallPackage = "";
                }
                if (!string.IsNullOrWhiteSpace(_staticPendingUninstallPackage))
                {
                    // "Yes" from checkbox → treat as "Administrator" (PSADT default context)
                    if (_cfg != null)
                        _cfg.UninstallPackage = _staticPendingUninstallPackage.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                            ? "Administrator"
                            : _staticPendingUninstallPackage;
                    _staticPendingUninstallPackage = "";
                }
                // Per-app AddInstallName / AddInstallTitle overrides global AutoPackage.json
                if (!string.IsNullOrWhiteSpace(_staticPendingAddInstallName))
                {
                    _cfg.AddInstallName = _staticPendingAddInstallName.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    _staticPendingAddInstallName = "";
                }
                if (!string.IsNullOrWhiteSpace(_staticPendingAddInstallTitle))
                {
                    _cfg.AddInstallTitle = _staticPendingAddInstallTitle.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    _staticPendingAddInstallTitle = "";
                }
                // ARP flags — per-app, only non-empty when TemplateFolder is set in app JSON
                _cfg.AddNoModify = _staticPendingAddNoModify.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                _cfg.AddNoRepair = _staticPendingAddNoRepair.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                _cfg.AddNoRemove = _staticPendingAddNoRemove.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                _staticPendingAddNoModify = "";
                _staticPendingAddNoRepair = "";
                _staticPendingAddNoRemove = "";

                // Apply per-app IntuneSettings overrides — these take priority over IntuneSettings.json
                if (_staticPendingIntuneOverrides != null)
                {
                    AutoIntuneSettingsOverride ov = _staticPendingIntuneOverrides;
                    _staticPendingIntuneOverrides = null;
                    if (!string.IsNullOrWhiteSpace(ov.InstallCommand))               _cfg.IntuneSettings.InstallCommand               = ov.InstallCommand;
                    if (!string.IsNullOrWhiteSpace(ov.UninstallCommand))             _cfg.IntuneSettings.UninstallCommand             = ov.UninstallCommand;
                    if (!string.IsNullOrWhiteSpace(ov.AllowAvailableUninstall))      _cfg.IntuneSettings.AllowAvailableUninstall      = ov.AllowAvailableUninstall;
                    if (!string.IsNullOrWhiteSpace(ov.InstallBehavior))              _cfg.IntuneSettings.InstallBehavior              = ov.InstallBehavior;
                    if (!string.IsNullOrWhiteSpace(ov.DeviceRestartBehaviour))       _cfg.IntuneSettings.DeviceRestartBehaviour       = ov.DeviceRestartBehaviour;
                    if (!string.IsNullOrWhiteSpace(ov.MinimumOperatingSystem))       _cfg.IntuneSettings.MinimumOperatingSystem       = ov.MinimumOperatingSystem;
                    if (!string.IsNullOrWhiteSpace(ov.OperatingSystemArchitecture))  _cfg.IntuneSettings.OperatingSystemArchitecture  = ov.OperatingSystemArchitecture;
                    if (!string.IsNullOrWhiteSpace(ov.AssignmentsRequired))          _cfg.IntuneSettings.AssignmentsRequired          = ov.AssignmentsRequired;
                    if (!string.IsNullOrWhiteSpace(ov.AssignmentsAvailable))         _cfg.IntuneSettings.AssignmentsAvailable         = ov.AssignmentsAvailable;
                    if (!string.IsNullOrWhiteSpace(ov.AssignmentsUninstall))         _cfg.IntuneSettings.AssignmentsUninstall         = ov.AssignmentsUninstall;
                    if (!string.IsNullOrWhiteSpace(ov.DetectionFileDetectionMethod))       _cfg.IntuneSettings.DetectionFileDetectionMethod       = ov.DetectionFileDetectionMethod;
                    if (!string.IsNullOrWhiteSpace(ov.DetectionFileOperator))        _cfg.IntuneSettings.DetectionFileOperator        = ov.DetectionFileOperator;
                    if (!string.IsNullOrWhiteSpace(ov.DetectionRegDetectionMethod))        _cfg.IntuneSettings.DetectionRegDetectionMethod        = ov.DetectionRegDetectionMethod;
                    if (!string.IsNullOrWhiteSpace(ov.DetectionRegOperator))         _cfg.IntuneSettings.DetectionRegOperator         = ov.DetectionRegOperator;
                    if (!string.IsNullOrWhiteSpace(ov.Developer))                    _cfg.IntuneSettings.Developer                    = ov.Developer;
                    if (!string.IsNullOrWhiteSpace(ov.Owner))                        _cfg.IntuneSettings.Owner                        = ov.Owner;
                    if (!string.IsNullOrWhiteSpace(ov.FeaturedApp))                  _cfg.IntuneSettings.FeaturedApp                  = ov.FeaturedApp;
                    // Category: per-app "IntuneCategory" maps to IntuneSettings.Category
                    if (!string.IsNullOrWhiteSpace(ov.Category))                     _cfg.IntuneSettings.Category                     = ov.Category;
                    // Description: per-app "Description" also feeds IntuneSettings.Description
                    if (!string.IsNullOrWhiteSpace(ov.Description))                  _cfg.IntuneSettings.Description                  = ov.Description;
                    // AssignmentsAvailable also drives DeployGroupName (group name for Intune assignment)
                    if (!string.IsNullOrWhiteSpace(ov.AssignmentsAvailable))         _cfg.DeployGroupName                             = ov.AssignmentsAvailable;
                    // Intune App Info overrides — fall back to AppName/AppVendor/AppVersion if blank
                    if (!string.IsNullOrWhiteSpace(ov.IntuneAppName))                _cfg.IntuneSettings.IntuneAppName                = ov.IntuneAppName;
                    if (!string.IsNullOrWhiteSpace(ov.IntuneAppVendor))              _cfg.IntuneSettings.IntuneAppVendor              = ov.IntuneAppVendor;
                    if (!string.IsNullOrWhiteSpace(ov.IntuneAppVersion))             _cfg.IntuneSettings.IntuneAppVersion             = ov.IntuneAppVersion;
                    if (!string.IsNullOrWhiteSpace(ov.InformationURL))               _cfg.IntuneSettings.InformationURL               = ov.InformationURL;
                    if (!string.IsNullOrWhiteSpace(ov.PrivacyURL))                   _cfg.IntuneSettings.PrivacyURL                   = ov.PrivacyURL;
                    if (!string.IsNullOrWhiteSpace(ov.Notes))                        _cfg.IntuneSettings.Notes                        = ov.Notes;
                    if (!string.IsNullOrWhiteSpace(ov.Logo))                         _cfg.IntuneSettings.Logo                         = ov.Logo;
                    if (!string.IsNullOrWhiteSpace(ov.InstallationTime))             _cfg.IntuneSettings.InstallationTime             = ov.InstallationTime;
                    if (!string.IsNullOrWhiteSpace(ov.UATUsers))                     _cfg.IntuneSettings.UATUsers                     = ov.UATUsers;
                }
                // Absorb PackageTemplateFolder — the per-app input folder for Invoke-AppDeployToolkit.ps1 + Files\
                // Only enable the copy if TemplateFolder was set (non-empty) in the app JSON.
                if (!string.IsNullOrWhiteSpace(_staticPendingPackageTemplateFolder))
                {
                    _pendingTemplateFolder   = _staticPendingPackageTemplateFolder;
                    _autoLogoPerAppFolder    = _staticPendingPackageTemplateFolder;   // remembered for the post-install Icon.png
                    _staticPendingPackageTemplateFolder = "";
                }
                _appTemplateFolderEnabled        = _staticPendingAppTemplateFolderEnabled;
                _staticPendingAppTemplateFolderEnabled = false;

                // Apply Template Folder FIRST — must be set before RebuildCommands runs
                if (_pendingTemplateFolderUI != null)
                {
                    if (TxtTemplateFolder    != null) TxtTemplateFolder.Text    = _pendingTemplateFolderUI;
                    if (ExeTxtTemplateFolder != null) ExeTxtTemplateFolder.Text = _pendingTemplateFolderUI;
                    _pendingTemplateFolderUI = "";
                }

                // Apply pending install/uninstall args NOW — after source loading is complete
                // so the ExeTxtSource_TextChanged pipeline cannot clear them afterwards.
                if (!string.IsNullOrWhiteSpace(_pendingInstallArgs) && ExeTxtInstallArgs != null)
                    ExeTxtInstallArgs.Text = _pendingInstallArgs;
                if (!string.IsNullOrWhiteSpace(_pendingUninstallArgs) && ExeTxtUninstallArgs != null)
                    ExeTxtUninstallArgs.Text = _pendingUninstallArgs;
                _pendingInstallArgs   = "";
                _pendingUninstallArgs = "";

                // Ensure the correct tab is active before clicking Create.
                // BtnCreate_Click checks TabExe.IsSelected to route to the right validator —
                // if the wrong tab is showing, validation fails even though source is loaded.
                bool exeLoaded = !string.IsNullOrWhiteSpace(_exePath);
                bool msiLoaded = !string.IsNullOrWhiteSpace(_msiPath);
                if (exeLoaded && TabExe != null && TabExe.IsSelected == false)
                    MainTabControl.SelectedIndex = 1; // force EXE tab
                else if (msiLoaded && TabMsi != null && TabMsi.IsSelected == false)
                    MainTabControl.SelectedIndex = 0; // force MSI tab

                // Apply pending package metadata from SourceDownload.json
                // (AppVendor → App Vendor, AppName → App Name, CloseApps → Close Applications)
                // Applied AFTER tab correction so we write to the right tab's textboxes.
                bool isExeTabNow = TabExe?.IsSelected == true;
                if (!string.IsNullOrWhiteSpace(_pendingAppVendor))
                {
                    if (isExeTabNow  && ExeTxtPublisher != null) ExeTxtPublisher.Text = _pendingAppVendor;
                    else if (!isExeTabNow && TxtPublisher != null) TxtPublisher.Text  = _pendingAppVendor;
                    _pendingAppVendor = "";
                }
                if (!string.IsNullOrWhiteSpace(_pendingAppName))
                {
                    if (isExeTabNow  && ExeTxtAppName != null) ExeTxtAppName.Text = _pendingAppName;
                    else if (!isExeTabNow && TxtAppName != null) TxtAppName.Text   = _pendingAppName;
                    _pendingAppName = "";
                }
                if (!string.IsNullOrWhiteSpace(_pendingCloseApps))
                {
                    if (isExeTabNow && ExeTxtCloseApps != null)  ExeTxtCloseApps.Text = _pendingCloseApps;
                    else if (!isExeTabNow && TxtCloseApps != null) TxtCloseApps.Text  = _pendingCloseApps;
                    _pendingCloseApps = "";
                }
                if (!string.IsNullOrWhiteSpace(_pendingAppVersion))
                {
                    if (isExeTabNow  && ExeTxtAppVersion != null) ExeTxtAppVersion.Text = _pendingAppVersion;
                    else if (!isExeTabNow && TxtAppVersion != null) TxtAppVersion.Text  = _pendingAppVersion;
                    _pendingAppVersion = "";
                }
                if (!string.IsNullOrWhiteSpace(_pendingUninstallPath))
                {
                    if (ExeTxtUninstallSource != null) ExeTxtUninstallSource.Text = _pendingUninstallPath;
                    _pendingUninstallPath = "";
                }

                // Apply CreateIntunewin, PublishToIntune, AssignToGroup from per-app JSON.
                // Write resolved values back to _cfg so BtnCreate_Click picks them up via _cfg
                // when _staticSelectedApp (perApp) is null (e.g. triggered from SourceDownloadPage).
                if (!string.IsNullOrWhiteSpace(_pendingCreateIntunewin))
                {
                    if (_cfg != null)
                        _cfg.CreateIntunewin = _pendingCreateIntunewin.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    _pendingCreateIntunewin = "";
                }
                if (!string.IsNullOrWhiteSpace(_pendingPublishToIntune))
                {
                    bool pubYes = _pendingPublishToIntune.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    if (_cfg != null)
                        _cfg.PublishToIntune = pubYes;
                    // PublishSettings=Yes is mapped to publishToIntune="Yes" by SourceDownloadPage.
                    // Ensure _autoTriggerSave reflects this so NavigateToPublishTab auto-saves the JSON.
                    if (pubYes) _autoTriggerSave = true;
                    _pendingPublishToIntune = "";
                }
                if (!string.IsNullOrWhiteSpace(_pendingAssignToGroup))
                {
                    if (_cfg != null)
                        _cfg.AssignToGroup = _pendingAssignToGroup.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    _pendingAssignToGroup = "";
                }

                // Force-rebuild commands now that all pending values are set
                if (isExeTabNow) ExeRebuildCommands();
                else RebuildCommands();

                // Rebuild the Package Name now that the final App Vendor / App Name /
                // App Version (from per-app metadata) have been applied. Without this the
                // Package Name could keep the value built during source load (before the
                // pending vendor/name/version overwrote the boxes), leaving stale data.
                if (isExeTabNow) ExeRebuildPackageName();
                else RebuildPackageName();

                // Apply DeploymentType to the named Deployment Type radio buttons on both tabs
                if (!string.IsNullOrWhiteSpace(_pendingDeploymentType))
                {
                    bool isRequired = _pendingDeploymentType.Equals("Required", StringComparison.OrdinalIgnoreCase);
                    _pendingDeploymentType = "";
                }

                // Apply DetectionType flags (MSI / File / Registry / Script)
                {
                    bool chkMsi      = _pendingDetTypeMSI     .Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    bool chkFile     = _pendingDetTypeFile    .Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    bool chkRegistry = _pendingDetTypeRegistry.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    // Store for Publish tab checkbox sync
                    _publishDetWantMsi  = chkMsi;
                    _publishDetWantFile = chkFile;
                    _publishDetWantReg  = chkRegistry;
                    bool chkScript   = _pendingDetTypeScript  .Equals("Yes", StringComparison.OrdinalIgnoreCase)
                                       || _pendingRulesFormat .Equals("Custom script", StringComparison.OrdinalIgnoreCase);
                    if (isExeTabNow)
                    {
                    }
                    else
                    {
                    }
                    _pendingDetTypeMSI = _pendingDetTypeFile = _pendingDetTypeRegistry = _pendingDetTypeScript = _pendingRulesFormat = "";
                    // Legacy fallback: honour _pendingDetectionType if set from old JSON
                    if (!string.IsNullOrWhiteSpace(_pendingDetectionType))
                    {
                        if (isExeTabNow)
                        {
                            bool legFile = _pendingDetectionType.Equals("File",     StringComparison.OrdinalIgnoreCase);
                            bool legReg  = _pendingDetectionType.Equals("Registry", StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            bool legMsi  = _pendingDetectionType.Equals("MSI",      StringComparison.OrdinalIgnoreCase);
                            bool legFile = _pendingDetectionType.Equals("File",     StringComparison.OrdinalIgnoreCase);
                            bool legReg  = _pendingDetectionType.Equals("Registry", StringComparison.OrdinalIgnoreCase);
                        }
                        _pendingDetectionType = "";
                    }
                }

                // Apply Description
                if (!string.IsNullOrWhiteSpace(_pendingDescription))
                {
                    _pendingDescription = "";
                }

                // Apply GroupName
                if (!string.IsNullOrWhiteSpace(_pendingGroupName))
                {
                    _pendingGroupName = "";
                }

                // Apply Dependency 1–4
                // Apply Auto Install Dependency 1–4 radio buttons
                // Apply Supersedence 1–4
                // Apply Uninstall Previous Version 1–4 radio buttons

                var btn = BtnCreate_Ctrl;
                if (btn != null && btn.IsEnabled)
                    btn.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            });
        }

        private void LoadSourceFromHeader(string src)
        {
            // Sync header textbox
            HdrTxtSource.TextChanged -= HdrTxtSource_TextChanged;
            HdrTxtSource.Text = src;
            HdrTxtSource.TextChanged += HdrTxtSource_TextChanged;

            bool isMsi = IsMsiSource(src);
            bool isExe = !isMsi && IsExeSource(src);

            if (isMsi)
            {
                // Switch to MSI tab
                MainTabControl.SelectedIndex = 0;
                LoadNewMsiSource(src);
                // FIX: Clear ExeTxtSource so stale EXE state (path, _exePath, _exeUninstallPath)
                // from a previous EXE-tab session cannot bleed into this MSI package run.
                // Without this, ExeTxtSource.Text could still hold a deep sub-folder path
                // (e.g. C:\Temp\down\FileCompare.exe) that was auto-resolved earlier, causing
                // BtnCreate_Exe to File.Copy that single EXE flat into Files\ if the tab
                // selection is ever evaluated as EXE.
                if (ExeTxtSource != null)
                {
                    ExeTxtSource.TextChanged -= ExeTxtSource_TextChanged;
                    ExeTxtSource.Clear();
                    ExeTxtSource.TextChanged += ExeTxtSource_TextChanged;
                }
                _exePath = "";
                _exeUninstallPath = "";
            }
            else if (isExe)
            {
                // Switch to EXE tab and let the existing TextChanged handler do all the work
                // (reads FileVersionInfo, registry, rebuilds commands, etc.)
                MainTabControl.SelectedIndex = 1;
                // FIX: Clear MSI source so stale MSI state cannot bleed into EXE runs.
                _msiPath = "";
                TxtSource.TextChanged -= TxtSource_TextChanged;
                TxtSource.Clear();
                TxtSource.TextChanged += TxtSource_TextChanged;
                if (ExeTxtSource != null)
                {
                    // Set text WITH handler attached so the full EXE-loading pipeline runs.
                    ExeTxtSource.Text = src;
                }
            }
            else
            {
                // Non-executable file (e.g. .ttf, .docx, .txt) --
                // clear all fields so stale data from the previous file is not shown.
                ClearMsiFields();
                ClearMsiDetectionFields();
                ExeClearFields();
                if (ExeTxtSource != null)
                {
                    ExeTxtSource.TextChanged -= ExeTxtSource_TextChanged;
                    ExeTxtSource.Clear();
                    ExeTxtSource.TextChanged += ExeTxtSource_TextChanged;
                }
                _msiPath = "";
                TxtSource.TextChanged -= TxtSource_TextChanged;
                TxtSource.Clear();
                TxtSource.TextChanged += TxtSource_TextChanged;
            }
        }

        /// <summary>Returns true if the source is an MSI file or a folder containing an MSI.</summary>
        private static bool IsMsiSource(string src)
        {
            if (File.Exists(src))
                return src.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(src))
                return Directory.GetFiles(src, "*.msi", SearchOption.AllDirectories).Any();
            return false;
        }

        /// <summary>Returns true if the source is a recognized executable install file or a folder.</summary>
        private static bool IsExeSource(string src)
        {
            // Folders are always accepted on the EXE tab (may contain exe/script)
            if (Directory.Exists(src)) return true;
            var exeExtensions = new[] { ".exe", ".ps1", ".bat", ".cmd", ".msp", ".appx", ".msix" };
            return File.Exists(src) && exeExtensions.Any(ext =>
                src.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        }

        // ── Shared clear-then-load entry point for MSI Browse and Drop ────────────
        // Bypasses TxtSource_TextChanged to avoid guard/partial-path issues.
        private void LoadNewMsiSource(string src)
        {
            string resolved = ResolveMsiFromSource(src);

            // Suppress TextChanged so it doesn't interfere while we set the text
            TxtSource.TextChanged -= TxtSource_TextChanged;
            TxtSource.Text = src;
            TxtSource.TextChanged += TxtSource_TextChanged;
            // Keep header in sync
            if (HdrTxtSource != null)
            {
                HdrTxtSource.TextChanged -= HdrTxtSource_TextChanged;
                HdrTxtSource.Text = src;
                HdrTxtSource.TextChanged += HdrTxtSource_TextChanged;
            }

            if (string.IsNullOrEmpty(resolved)) return;

            // Always clear then load — even if the resolved path looks the same,
            // the user explicitly re-browsed so we must reload fresh data.
            _msiPath = "";              // reset so TextChanged guard never blocks
            _sourceFullyLoaded = false; // reset - will be set when ApplyMsiProps completes
            ClearMsiFields();
            ClearMsiDetectionFields();
            _msiPath = resolved;
            LoadMsiPropertiesAsync(resolved);
        }

        private void BtnCancelSource_Click(object sender, RoutedEventArgs e)
        {
            _msiPath = "";
            TxtSource.Clear();
            if (HdrTxtSource != null)
            {
                HdrTxtSource.TextChanged -= HdrTxtSource_TextChanged;
                HdrTxtSource.Clear();
                HdrTxtSource.TextChanged += HdrTxtSource_TextChanged;
            }
            ClearMsiFields();
            ClearMsiDetectionFields();
        }

        private void BtnBrowseTemplate_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(
                Window.GetWindow(this)!).Handle;
            string? picked = ShellFolderPicker.Browse(hwnd,
                "Select Template Folder", @"C:\");
            if (picked != null)
            {
                TxtTemplateFolder.Text = picked;
                UpdatePanelVisibility(); // re-check for Invoke-AppDeployToolkit.ps1
            }
        }

        private void BtnClearTemplate_Click(object sender, RoutedEventArgs e)
            => TxtTemplateFolder.Clear();

        // ── Generic field clear — wired to every inline ✕ button via Tag=TextBoxName ──
        private void FieldClear_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string name = btn.Tag?.ToString() ?? "";
            if (string.IsNullOrEmpty(name)) return;

            var tb = FindName(name) as TextBox
                  ?? FindVisualChild<TextBox>(this, name);
            if (tb != null)
            {
                tb.Clear();
                tb.Focus();
            }
        }

        // Walk visual tree to find a named TextBox (for template-hosted controls)
        private static TextBox? FindVisualChild<T>(System.Windows.DependencyObject parent, string name)
            where T : System.Windows.DependencyObject
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is TextBox tb && tb.Name == name) return tb;
                var result = FindVisualChild<T>(child, name);
                if (result != null) return result;
            }
            return null;
        }

        // ── Tab button handlers removed — tabs are auto-switched by source extension ──
        // UpdateTabButtonStyles is a no-op kept so any surviving call sites compile.
        private void BtnTabMsi_Click(object sender, RoutedEventArgs e)
            => MainTabControl.SelectedIndex = 0;

        private void BtnTabExe_Click(object sender, RoutedEventArgs e)
            => MainTabControl.SelectedIndex = 1;

        private void UpdateTabButtonStyles() { /* no-op: tab buttons removed */ }


        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            // Reload JSON then re-apply all defaults — keeps source/package fields intact
            LoadConfig();
            ApplyConfigDefaults();
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            // Always clear the header source box
            if (HdrTxtSource != null)
            {
                HdrTxtSource.TextChanged -= HdrTxtSource_TextChanged;
                HdrTxtSource.Clear();
                HdrTxtSource.TextChanged += HdrTxtSource_TextChanged;
            }
            bool isExe = TabExe?.IsSelected == true;
            if (isExe)
            {
                // Clear user-entered source/app fields only
                ExeTxtSource?.Clear();
                ExeTxtUninstallSource?.Clear();
                ExeTxtInstallArgs?.Clear();
                ExeTxtUninstallArgs?.Clear();
                ExeClearFields();
                ClearExeDetectionFields();
            }
            else
            {
                // Clear user-entered source/app fields and all detection fields
                TxtSource.Clear();
                if (TxtMsiArgumentList != null) TxtMsiArgumentList.Clear();
                if (TxtAdditionalArgs  != null) TxtAdditionalArgs.Clear();
                ClearMsiFields();
                ClearMsiDetectionFields();
            }

            // Re-apply defaults from AutoPackage.json (template folder, combos,
            // group name, description, install/uninstall commands, detection operators)
            LoadConfig();
            ApplyConfigDefaults();
        }

        // ── Clears all EXE detection panel fields ─────────────────────────────
        private void ClearExeDetectionFields()
        {
            // Reset detection type to File (default for EXE)
        }

        // ══════════════════════════════════════════════════════════════════
        //  RESET ALL TABS
        //  Called by SourceDownloadPage whenever a download link button is
        //  clicked, so that Package / Execute / Intunewin / Publish tabs
        //  never show stale data from a previous run.
        // ══════════════════════════════════════════════════════════════════
        public void ResetAllTabs()
        {
            Dispatcher.Invoke(() =>
            {
                // ── Package tab (EXE + MSI) ───────────────────────────────────
                if (HdrTxtSource != null)
                {
                    HdrTxtSource.TextChanged -= HdrTxtSource_TextChanged;
                    HdrTxtSource.Clear();
                    HdrTxtSource.TextChanged += HdrTxtSource_TextChanged;
                }
                // EXE side
                ExeTxtSource?.Clear();
                ExeTxtUninstallSource?.Clear();
                ExeTxtInstallArgs?.Clear();
                ExeTxtUninstallArgs?.Clear();
                ExeClearFields();
                ClearExeDetectionFields();
                // MSI side
                TxtSource?.Clear();
                if (TxtMsiArgumentList != null) TxtMsiArgumentList.Clear();
                if (TxtAdditionalArgs  != null) TxtAdditionalArgs.Clear();
                ClearMsiFields();
                ClearMsiDetectionFields();

                // Re-apply JSON defaults so combos / template folder are correct
                LoadConfig();
                ApplyConfigDefaults();

                // ── Force-clear App Vendor + Package Name (both tabs) ─────────────
                // ExeClearFields/ClearMsiFields reset App Vendor to the config default and
                // ApplyConfigDefaults can re-set it, so a fresh download click could still
                // show the previous run's vendor (and a stale Package Name). Blank them
                // outright here — the new download repopulates them from the source file /
                // per-app metadata. ResetAllTabs runs ONLY on a download-button click, so
                // clearing here does not affect manual browsing.
                if (ExeTxtPublisher   != null) ExeTxtPublisher.Clear();
                if (ExeTxtPackageName != null) ExeTxtPackageName.Clear();
                if (TxtPublisher      != null) TxtPublisher.Clear();
                if (TxtPackageName    != null) TxtPackageName.Clear();

                // ── Execute tab ───────────────────────────────────────────────
                // Cancel any in-progress execution and reset its UI
                try { _createCts?.Cancel(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                _createCts = null;
                _createPause.Reset();
                _onAfterCreateComplete  = null;
                _pendingAfterExecuteChain = null;
                _pendingAfterPublishUninstallExecute = null;
                _triggerIntunewinTab = false;
                _triggerPublishTab   = false;

                // Reset AutoExecutePage: cancel process, clear script path + log
                try { _autoExecPage?.CancelAndClear(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                // ── Intunewin tab ─────────────────────────────────────────────
                // Delegate cancel + clear to AutoIntunewinPage
                try { _autoIntunewinPage?.CancelAndClear(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } // AutoIntunewinPage handles Iw cancel+clear

                // ── Publish tab ───────────────────────────────────────────────
                // Clear the state that BtnTabPublish_Click uses to decide whether
                // to call NavigateToPublishTab (which re-fills all fields).
                // Without this, navigating to the Publish tab after a Download
                // immediately re-populates data from the previous run's folder.
                LastCreatedPackageFolder = "";
                _publishCapAppName       = "";
                _publishCapPublisher     = "";
                _publishCapVersion       = "";
                _publishCapDesc          = "";
                _autoTriggerSave         = false;
                _autoTriggerPublish      = false;
                _lastCreatedIntunewinPath = "";

                // Delegate UI clear to AutoPublishPage (all 8 sub-tabs)
                try { _autoPublishPage?.ClearAll(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                // Clear deferred uninstall static state so previous run's command
                // doesn't fire on the next publish
                _staticDeferredUninstallSourceCmd      = "";
                _staticDeferredUninstallSourceLazy     = false;
                _staticDeferredUninstallSourceLazyArgs = "";
                _publishActionHasDeferredUninstall     = false;
                _intunewinIsTerminus                   = false;
                _publishToIntuneIsTerminus             = false;
            });
        }
        // ══════════════════════════════════════════════════════════════════
        private void TxtSource_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        // ── Page-wide drag & drop — works anywhere on both MSI and EXE tabs ──
        // ══════════════════════════════════════════════════════════════════
        //  PAGE-LEVEL DRAG & DROP  (PreviewDragOver / PreviewDrop — tunnel phase)
        //  Handles ALL textboxes in one place: Install Source, Uninstall Source,
        //  Template Folder (MSI tab), Template Folder (EXE tab), and page background.
        // ══════════════════════════════════════════════════════════════════
        private void Page_DragOver(object sender, DragEventArgs e)
        {
            bool hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;   // Must be true in tunnel phase to show cursor everywhere
        }

        private void Page_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Handled = true; return; }
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) { e.Handled = true; return; }
            string dropped = files[0];
            e.Handled = true;

            // Walk up the visual tree from the drop target to find the nearest named TextBox
            var target = FindDropTargetTextBox(e.OriginalSource as System.Windows.DependencyObject);

            switch (target)
            {
                // ── Install Source (header) ───────────────────────────────────────
                case "HdrTxtSource":
                    LoadSourceFromHeader(dropped);
                    break;

                // ── MSI tab: hidden proxy (also routes to header) ─────────────────
                case "TxtSource":
                    LoadSourceFromHeader(dropped);
                    break;

                // ── EXE Uninstall Source ──────────────────────────────────────────
                case "ExeTxtUninstallSource":
                    ExeTxtUninstallSource.Text = dropped;
                    _exeUninstallPath = dropped;
                    ExeRebuildCommands();
                    break;

                // ── MSI Template Folder ───────────────────────────────────────────
                case "TxtTemplateFolder":
                    if (Directory.Exists(dropped))      TxtTemplateFolder.Text = dropped;
                    else if (File.Exists(dropped))       TxtTemplateFolder.Text = IOPath.GetDirectoryName(dropped) ?? dropped;
                    UpdatePanelVisibility();
                    break;

                // ── EXE Template Folder ───────────────────────────────────────────
                case "ExeTxtTemplateFolder":
                    if (ExeTxtTemplateFolder != null)
                    {
                        if (Directory.Exists(dropped))  ExeTxtTemplateFolder.Text = dropped;
                        else if (File.Exists(dropped))  ExeTxtTemplateFolder.Text = IOPath.GetDirectoryName(dropped) ?? dropped;
                    }
                    break;

                // ── EXE hidden source proxy ───────────────────────────────────────
                case "ExeTxtSource":
                    LoadSourceFromHeader(dropped);
                    break;

                // ── Anywhere else on the page → route to header ───────────────────
                default:
                    LoadSourceFromHeader(dropped);
                    break;
            }
        }

        /// <summary>
        /// Walks up the visual tree from the drop target and returns the x:Name
        /// of the first TextBox ancestor that has AllowDrop=true, or null.
        /// </summary>
        private static string? FindDropTargetTextBox(System.Windows.DependencyObject? element)
        {
            var current = element;
            while (current != null)
            {
                if (current is TextBox tb && tb.AllowDrop && tb.Name is { Length: > 0 } name)
                    return name;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // Walk visual tree upward to find a parent of type T
        private static T? FindParent<T>(System.Windows.DependencyObject child)
            where T : System.Windows.DependencyObject
        {
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is T found) return found;
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        // Generic drag-over for Template Folder textboxes (kept for any direct XAML wiring)
        private void TemplateTxt_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void MsiTemplateTxt_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files?.Length > 0)
            {
                string dropped = files[0];
                if (Directory.Exists(dropped)) TxtTemplateFolder.Text = dropped;
                else if (File.Exists(dropped))  TxtTemplateFolder.Text = IOPath.GetDirectoryName(dropped) ?? dropped;
                UpdatePanelVisibility();
            }
            e.Handled = true;
        }

        private void ExeTemplateTxt_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files?.Length > 0 && ExeTxtTemplateFolder != null)
            {
                string dropped = files[0];
                if (Directory.Exists(dropped)) ExeTxtTemplateFolder.Text = dropped;
                else if (File.Exists(dropped))  ExeTxtTemplateFolder.Text = IOPath.GetDirectoryName(dropped) ?? dropped;
            }
            e.Handled = true;
        }

        private void TxtSource_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files?.Length > 0) LoadNewMsiSource(files[0]);
            e.Handled = true;
        }

        // ══════════════════════════════════════════════════════════════════
        //  TEXT CHANGED — Source field
        // ══════════════════════════════════════════════════════════════════
        private void TxtSource_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtSourceError.Visibility = Visibility.Collapsed;
            string src = TxtSource.Text.Trim();
            if (string.IsNullOrWhiteSpace(src)) { ClearMsiFields(); ClearMsiDetectionFields(); return; }

            // Resolve the MSI path from the typed/pasted/dropped value
            string resolved = ResolveMsiFromSource(src);

            // Only clear + reload when the resolved file actually changes.
            // This avoids clearing when the user is still typing (partial paths
            // resolve to empty) and avoids double-loading the same file.
            if (string.IsNullOrEmpty(resolved) || string.Equals(resolved, _msiPath,
                StringComparison.OrdinalIgnoreCase))
                return;

            // New file detected — clear all previous data then load the new file
            ClearMsiFields();
            ClearMsiDetectionFields();
            _msiPath = resolved;
            LoadMsiPropertiesAsync(resolved);
        }

        private void TxtTemplateFolder_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePanelVisibility();
            RebuildCommands();  // show/hide Install/Uninstall based on template selection
        }

        // ── Show/hide Install/Uninstall command rows based on template folder ──────
        private void UpdateCommandRowVisibility(bool show)
        {
            // Install Command / Uninstall Command rows — ALWAYS shown regardless of template.
            // When no template: shows direct msiexec commands (e.g. filecompare.msi /qn).
            // When template: shows PSADT powershell commands.
            var alwaysVis = Visibility.Visible;
            if (LblInstallCmd   != null) LblInstallCmd.Visibility   = alwaysVis;
            if (FindName("GridInstallCmd")   is FrameworkElement gic)  gic.Visibility  = alwaysVis;
            if (LblUninstallCmd != null) LblUninstallCmd.Visibility = alwaysVis;
            if (FindName("GridUninstallCmd") is FrameworkElement guic) guic.Visibility = alwaysVis;
            // Install Package / Uninstall Package rows — only visible when template folder is set
            var pkgVis = show ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Show/hide EXE tab Install/Uninstall command rows based on template folder ──
        private void UpdateExeCommandRowVisibility(bool show)
        {
            // Install Command / Uninstall Command rows — ALWAYS shown regardless of template.
            var alwaysVis = Visibility.Visible;
            if (FindName("ExeLblInstallCmd")    is FrameworkElement eli)  eli.Visibility  = alwaysVis;
            if (FindName("ExeGridInstallCmd")   is FrameworkElement egi)  egi.Visibility  = alwaysVis;
            if (FindName("ExeLblUninstallCmd")  is FrameworkElement elu)  elu.Visibility  = alwaysVis;
            if (FindName("ExeGridUninstallCmd") is FrameworkElement egu)  egu.Visibility  = alwaysVis;
            // Install Package / Uninstall Package rows — only visible when template folder is set
            var pkgVis = show ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Auto-increment App Revision until the package folder is free ──────────────
        // Called only when triggered from Source Download (Create Package checked).
        // Increments the MSI-tab or EXE-tab revision textbox (e.g. 01 → 02 → 03…)
        // and rebuilds the package name until no folder with that name exists.
        // Returns the final (unique) package name.
        private string AutoIncrementRevisionUntilFree(string rootBase, bool isExeTab)
        {
            // Read current revision from the active tab's textbox
            TextBox? revBox = isExeTab ? ExeTxtAppRevision : TxtAppRevision;
            TextBox? pkgBox = isExeTab ? ExeTxtPackageName : TxtPackageName;

            string currentRev = revBox?.Text.Trim() ?? "01";
            if (string.IsNullOrWhiteSpace(currentRev)) currentRev = "01";

            // Parse the numeric part (handles "01", "02", "1", "2" etc.)
            if (!int.TryParse(currentRev, out int revNum)) revNum = 1;

            string packageName = pkgBox?.Text.Trim() ?? "";

            // Keep incrementing until a free folder is found.
            // Cap raised well past 99 so revisions can grow to 100, 101, … without
            // stopping at 99 (previous cap of 98 iterations blocked the 99→100 step).
            for (int attempt = 0; attempt < 100000; attempt++)
            {
                string checkPath = IOPath.Combine(rootBase, packageName);
                if (!Directory.Exists(checkPath)) break;

                // Folder exists — bump revision
                revNum++;
                // "D2" pads to a MINIMUM of 2 digits (01, 02 … 99) and does NOT
                // truncate, so 100, 101, … render in full once we pass 99.
                string newRev = revNum.ToString("D2");

                // Update the revision textbox on UI thread (we're already on UI thread here)
                if (revBox != null) revBox.Text = newRev;

                // Rebuild package name with the new revision
                if (isExeTab) ExeRebuildPackageName(); else RebuildPackageName();

                packageName = pkgBox?.Text.Trim() ?? "";
            }

            return packageName;
        }

        // ── Resolve MSI: either the path itself or the first .msi in folder ──
        private static string ResolveMsiFromSource(string src)
        {
            if (File.Exists(src) &&
                src.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                return src;
            if (Directory.Exists(src))
            {
                var found = Directory.GetFiles(src, "*.msi", SearchOption.AllDirectories)
                                     .FirstOrDefault();
                if (found != null) return found;
            }
            return "";
        }

        // ══════════════════════════════════════════════════════════════════
        //  MSI PROPERTY READING  (COM — same pattern as CreatePackagePage)
        // ══════════════════════════════════════════════════════════════════
        private void LoadMsiPropertiesAsync(string msiPath)
        {
            int myGen = ++_msiLoadGen;
            // ReadMsiProperties uses WindowsInstaller COM which requires STA apartment.
            // ThreadPool threads are MTA — run on a dedicated STA thread.
            var staThread = new System.Threading.Thread(() =>
            {
                var props = ReadMsiProperties(msiPath);
                props.TryGetValue("ProductCode", out var pc);
                props.TryGetValue("ProductName", out var pn);
                // Primary: product code key lookup
                var reg = LookupInstalledByProductCode(pc ?? "");
                // Fallback: ProductName hint (handles apps whose registry key != product code)
                if (reg == null && !string.IsNullOrWhiteSpace(pn))
                    reg = LookupInstalledByExe("", pn);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Discard if user has already browsed a newer MSI
                    if (myGen != _msiLoadGen) return;
                    ApplyMsiProps(props, reg);
                }));
            });
            staThread.SetApartmentState(System.Threading.ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();
        }

        private static Dictionary<string, string> ReadMsiProperties(string msiPath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            object? inst = null, db = null, vw = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WindowsInstaller.Installer")!;
                inst = Activator.CreateInstance(t)!;
                db   = t.InvokeMember("OpenDatabase",  BindingFlags.InvokeMethod, null, inst,
                           new object[] { msiPath, 0 });
                var dt = db!.GetType();
                vw   = dt.InvokeMember("OpenView", BindingFlags.InvokeMethod, null, db,
                           new object[] { "SELECT Property,Value FROM Property" });
                var vt = vw!.GetType();
                vt.InvokeMember("Execute", BindingFlags.InvokeMethod, null, vw, null);
                while (true)
                {
                    object? rec = vt.InvokeMember("Fetch", BindingFlags.InvokeMethod, null, vw, null);
                    if (rec == null) break;
                    var rt = rec.GetType();
                    string k  = rt.InvokeMember("StringData", BindingFlags.GetProperty,
                                    null, rec, new object[] { 1 }) as string ?? "";
                    string v2 = rt.InvokeMember("StringData", BindingFlags.GetProperty,
                                    null, rec, new object[] { 2 }) as string ?? "";
                    result[k] = v2;
                    Marshal.ReleaseComObject(rec);
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            finally
            {
                if (vw   != null) try { Marshal.ReleaseComObject(vw);   } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                if (db   != null) try { Marshal.ReleaseComObject(db);   } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                if (inst != null) try { Marshal.ReleaseComObject(inst); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            return result;
        }

        // ── STA-safe wrapper — always runs on a fresh STA thread (required by COM) ──
        private static Dictionary<string, string> ReadMsiPropertiesSTA(string msiPath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var t = new System.Threading.Thread(() => { result = ReadMsiProperties(msiPath); });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            t.Join();
            return result;
        }

        private void ApplyMsiProps(Dictionary<string, string> props, ExeInstalledInfo? reg = null)
        {
            _suppressNameRebuild = true;

            // MSI properties as baseline
            if (props.TryGetValue("Manufacturer",   out var mfr) && !string.IsNullOrWhiteSpace(mfr))
                TxtPublisher.Text = mfr.Trim();
            if (props.TryGetValue("ProductName",    out var pn)  && !string.IsNullOrWhiteSpace(pn))
                TxtAppName.Text   = pn.Trim();
            if (props.TryGetValue("ProductVersion", out var pv)  && !string.IsNullOrWhiteSpace(pv))
                TxtAppVersion.Text = pv.Trim();
            if (props.TryGetValue("ProductCode",    out var pc)  && !string.IsNullOrWhiteSpace(pc))
            {
                TxtProductCode.Text = pc.Trim();
            }
            if (TxtAppRevision != null && string.IsNullOrWhiteSpace(TxtAppRevision.Text))
                TxtAppRevision.Text = "01";

            // ── Also set MSI version check and version value ────────────────
            if (props.TryGetValue("ProductVersion", out var pvCheck) && !string.IsNullOrWhiteSpace(pvCheck))
            {
            }

            // ── Derive install folder from MSI properties directly ──────────
            // Used as fallback when the app is not yet installed on this machine.
            string msiInstallDir = "";
            if (props.TryGetValue("ARPINSTALLLOCATION", out var arpLoc) && !string.IsNullOrWhiteSpace(arpLoc))
                msiInstallDir = arpLoc.TrimEnd(IOPath.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(msiInstallDir) &&
                props.TryGetValue("INSTALLDIR", out var instDir) && !string.IsNullOrWhiteSpace(instDir))
                msiInstallDir = instDir.TrimEnd(IOPath.DirectorySeparatorChar);
            // Derive from ProductName + ProgramFiles as last resort
            if (string.IsNullOrWhiteSpace(msiInstallDir) && !string.IsNullOrWhiteSpace(TxtAppName.Text))
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string appDir = TxtAppName.Text.Trim();
                string pub = TxtPublisher.Text.Trim();
                // Try Publisher\AppName and AppName under both ProgramFiles
                foreach (var root in new[] { pf, pf86 })
                {
                    string[] candidates = {
                        IOPath.Combine(root, pub, appDir),
                        IOPath.Combine(root, appDir),
                        IOPath.Combine(root, pub)
                    };
                    foreach (var c in candidates)
                    {
                        if (Directory.Exists(c)) { msiInstallDir = c; break; }
                    }
                    if (!string.IsNullOrWhiteSpace(msiInstallDir)) break;
                }
            }

            // ── Build file detection version from ProductVersion ─────────────
            string msiVersion = "";
            if (props.TryGetValue("ProductVersion", out var prodVer) && !string.IsNullOrWhiteSpace(prodVer))
                msiVersion = prodVer.Trim();

            // Registry enrichment — prefer DisplayName/Publisher/DisplayVersion if more accurate
            if (reg != null)
            {
                if (!string.IsNullOrWhiteSpace(reg.DisplayName))    TxtAppName.Text    = reg.DisplayName;
                if (!string.IsNullOrWhiteSpace(reg.Publisher))      TxtPublisher.Text  = reg.Publisher;
                if (!string.IsNullOrWhiteSpace(reg.DisplayVersion)) TxtAppVersion.Text = reg.DisplayVersion;

                // File detection fields — prefer registry, fall back to MSI props
                string filePath = !string.IsNullOrWhiteSpace(reg.InstallLocation) ? reg.InstallLocation : msiInstallDir;

                // File or folder — prefer registry MainExePath, then scan install folder
                string mainExeName = !string.IsNullOrWhiteSpace(reg.MainExePath)
                    ? IOPath.GetFileName(reg.MainExePath) : "";
                if (string.IsNullOrWhiteSpace(mainExeName) && Directory.Exists(filePath))
                    mainExeName = FindLargestExe(filePath);

                string fileVer = !string.IsNullOrWhiteSpace(reg.MainExeVersion) ? reg.MainExeVersion : msiVersion;

                // Registry detection fields — use actual key path from lookup

                // Close Applications — auto-populate from main exe name
                if (TxtCloseApps != null && string.IsNullOrWhiteSpace(TxtCloseApps.Text))
                {
                    string closeApp = DeriveCloseApps(reg);
                    if (!string.IsNullOrWhiteSpace(closeApp))
                        TxtCloseApps.Text = closeApp;
                }
            }
            else
            {
                // App not found in registry — detection fields not populated (controls removed)
            }

            // Re-apply detection method + operator from config (overrides XAML defaults)

            _suppressNameRebuild = false;
            RebuildPackageName();
            RebuildCommands();
            UpdateDetectionText();
            _sourceFullyLoaded = true; // MSI props fully applied - safe to trigger Create
        }

        private void ClearMsiFields()
        {
            _msiPath = "";
            ++_msiLoadGen;   // cancel any in-flight background MSI load
            _suppressNameRebuild = true;

            // ── Clear MSI-derived fields ──────────────────────────────────────
            TxtAppName.Clear();
            TxtAppVersion.Clear();
            TxtProductCode.Clear();
            if (TxtPublisher != null) TxtPublisher.Clear();   // App Vendor
            if (TxtAppRevision != null) TxtAppRevision.Clear();
            TxtPackageName.Clear();
            if (TxtCloseApps != null) TxtCloseApps.Clear();

            // ── Restore all JSON-backed fields to config defaults ─────────────
            // This ensures every browse/rebrowse starts from a clean, consistent
            // state driven entirely by AutoPackage.json — not by prior user edits.

            // Template folder
            TxtTemplateFolder.Text = _cfg?.TemplateFolderPath ?? "";

            // MSI / Additional argument lists
            if (TxtMsiArgumentList != null) TxtMsiArgumentList.Clear();
            if (TxtAdditionalArgs  != null) TxtAdditionalArgs.Clear();

            // Combo dropdowns
            _suppressVisibilityUpdate = true;
            SetCombo(CmbCreateMst,       _cfg?.CreateMST             ?? false);
            SetCombo(CmbRemoveDesktop,   _cfg?.RemoveDesktopShortcut ?? false);
            _suppressVisibilityUpdate = false;

            // App Vendor (the package/app vendor — NOT the Intune "Developer" field).
            // Prefer the explicit vendor; fall back to legacy Publisher. Never use
            // Developer here (that is a separate Intune App Info field).
            string publisher = !string.IsNullOrWhiteSpace(_cfg?.IntuneSettings?.IntuneAppVendor)
                ? _cfg.IntuneSettings.IntuneAppVendor
                : _cfg?.IntuneSettings?.Publisher ?? "";
            TxtPublisher.Text = publisher;

            // Description

            // Group name
            string groupName = !string.IsNullOrWhiteSpace(_cfg?.DeployGroupName)
                ? _cfg.DeployGroupName
                : _cfg?.IntuneSettings?.AssignmentsAvailable ?? "";

            // Install / Uninstall commands — always show rows.
            // When template folder is set: use PSADT commands from IntuneSettings.json.
            // When no template: leave empty — RebuildCommands() fills them from MSI/EXE source.
            bool tplSet = !string.IsNullOrWhiteSpace(_cfg?.TemplateFolderPath);
            if (tplSet)
            {
                if (TxtInstallCmd   != null) TxtInstallCmd.Text   = _cfg?.IntuneSettings?.InstallCommand   ?? "";
                if (TxtUninstallCmd != null) TxtUninstallCmd.Text = _cfg?.IntuneSettings?.UninstallCommand ?? "";
            }
            UpdateCommandRowVisibility(tplSet);

            _suppressNameRebuild = false;
        }

        // ── Clears all MSI detection panel fields (all three rule types) ──────
        private void ClearMsiDetectionFields()
        {
            // MSI rule panel

            // File rule panel

            // Registry rule panel
        }

        // ══════════════════════════════════════════════════════════════════
        //  AUTO-BUILD: Package Name, Commands, Detection
        // ══════════════════════════════════════════════════════════════════
        private void AppField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressNameRebuild) return;
            RebuildPackageName();
            RebuildCommands();
        }

        private void ProductCode_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressNameRebuild) return;
            RebuildCommands();
            UpdateDetectionText();
        }

        private void MsiArgs_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressNameRebuild) return;
            RebuildCommands();
        }

        private void RebuildPackageName()
        {
            if (TxtPackageName == null || TxtPublisher == null || TxtAppName == null || TxtAppVersion == null) return;
            string vendor   = Sanitize(TxtPublisher.Text);
            string name     = Sanitize(TxtAppName.Text);
            string version  = Sanitize(TxtAppVersion.Text);
            string revision = Sanitize(TxtAppRevision?.Text ?? "01");
            if (string.IsNullOrEmpty(revision)) revision = "01";
            var parts = new[] { vendor, name, version, revision }
                        .Where(p => !string.IsNullOrEmpty(p));
            TxtPackageName.Text = string.Join("_", parts);
        }

        private static string Sanitize(string s)
            // Keep () and [] so a package folder matches an app name like "FileCompare(x64)" or
            // "App[x64]" (both are valid folder-name characters). Spaces are still removed.
            => System.Text.RegularExpressions.Regex
                  .Replace(s?.Trim() ?? "", @"[^\w\.\-()\[\]]", "");

        private void RebuildCommands()
        {
            if (TxtInstallCmd == null || TxtUninstallCmd == null) return;

            string tpl         = TxtTemplateFolder?.Text.Trim() ?? "";
            bool hasTemplate   = !string.IsNullOrWhiteSpace(tpl);

            // When template folder is set: IntuneSettings.json PSADT commands take priority.
            // When NO template: always build direct MSI commands from source file + silent args.

            bool createMst     = ComboIsYes(CmbCreateMst);
            string msiName     = string.IsNullOrEmpty(_msiPath) ? "" : IOPath.GetFileName(_msiPath);
            string mstName     = string.IsNullOrEmpty(msiName)  ? "" : IOPath.GetFileNameWithoutExtension(msiName) + ".mst";
            string pc          = TxtProductCode?.Text.Trim() ?? "";
            string msiArgList  = TxtMsiArgumentList?.Text.Trim()  ?? "";
            string addArgs     = TxtAdditionalArgs?.Text.Trim()   ?? "";

            if (hasTemplate)
            {
                UpdateCommandRowVisibility(true);
                bool instFromJson   = !string.IsNullOrWhiteSpace(_cfg?.IntuneSettings?.InstallCommand);
                bool uninstFromJson = !string.IsNullOrWhiteSpace(_cfg?.IntuneSettings?.UninstallCommand);
                if (!instFromJson)
                    TxtInstallCmd.Text   = !string.IsNullOrWhiteSpace(_staticSelectedApp?.PSADTInstallCommand)
                        ? _staticSelectedApp.PSADTInstallCommand
                        : "powershell.exe -ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"Invoke-AppDeployToolkit.ps1\" -DeploymentType Install";
                if (!uninstFromJson)
                    TxtUninstallCmd.Text = !string.IsNullOrWhiteSpace(_staticSelectedApp?.PSADTUninstallCommand)
                        ? _staticSelectedApp.PSADTUninstallCommand
                        : "powershell.exe -ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"Invoke-AppDeployToolkit.ps1\" -DeploymentType Uninstall";
            }
            else if (!string.IsNullOrEmpty(msiName))
            {
                // No template — show command rows with direct msiexec-style commands
                // Install:   <msiname> <SilentInstallParameterMSI>   e.g. filecompare-1.0-x64.msi /qn
                // Uninstall: <productcode> <SilentUninstallParameterMSI>  e.g. {5DFCA3BF-...} /qn
                UpdateCommandRowVisibility(true);
                // Use pending MSI args from per-app JSON if available, else fall back to UI fields
                string effectiveInstallArgs   = !string.IsNullOrWhiteSpace(_pendingMsiInstallArgs)
                    ? _pendingMsiInstallArgs
                    : (!string.IsNullOrWhiteSpace(msiArgList) ? msiArgList : "/qn /norestart");
                string effectiveUninstallArgs = !string.IsNullOrWhiteSpace(_pendingMsiUninstallArgs)
                    ? _pendingMsiUninstallArgs
                    : "/qn /norestart";

                // Format: <msiname> <SilentInstallParameterMSI>
                TxtInstallCmd.Text = $"{msiName} {effectiveInstallArgs}".Trim();

                // Format: <productcode> <SilentUninstallParameterMSI>
                string uninstTarget = string.IsNullOrWhiteSpace(pc) ? $"\"{msiName}\"" : pc;
                TxtUninstallCmd.Text = $"{uninstTarget} {effectiveUninstallArgs}".Trim();
            }
            else
            {
                UpdateCommandRowVisibility(true);
                TxtInstallCmd.Text   = "";
                TxtUninstallCmd.Text = "";
            }
        }

        /// <summary>
        /// After PublishDeployPage.RebuildDetectionRuleUI() runs, walk DetectionRulesList
        /// and replace the radio button in Col 0 of each detection rule card
        /// with a CheckBox whose checked state reflects the per-app JSON DetectionType values.
        /// </summary>
        /// <summary>
        /// For File detection rules using a Date method, splits the combined date+time TextBox
        /// into two separate TextBoxes (date | time), matching the Download tab layout.
        /// Called after RebuildDetectionRuleUI from AutoPackagePage.
        /// </summary>
        /// <summary>
        /// After RebuildDetectionRuleUI, replaces the File operator ComboBox items with
        /// Intune's standard operator list (Equals, Not equal to, Greater than or equal to, etc.)
        /// regardless of detection method (Date or otherwise).
        /// </summary>
        /// <summary>
        /// After RebuildDetectionRuleUI, replaces all operator ComboBox items (MSI, File, Registry)
        /// with Intune's standard operator list to match the Intune portal exactly.
        /// </summary>
        private void FixFileOperatorOptionsInPublishPage(dynamic publishPage)
        {
            try
            {
                if (publishPage?.FindName("DetectionRulesList") is not System.Windows.Controls.Panel list) return;
                var intuneOps = new[] { "Equals", "Not equal to", "Greater than or equal to", "Greater than", "Less than or equal to", "Less than" };

                foreach (System.Windows.UIElement child in list.Children)
                {
                    if (child is not System.Windows.Controls.Border card) continue;
                    if (card.Child is not System.Windows.Controls.Grid cardGrid) continue;
                    if (cardGrid.Children.Count < 2) continue;
                    if (cardGrid.Children[1] is not System.Windows.Controls.StackPanel fields) continue;

                    // Get the rule data to know what operator to select
                    string? ruleFileOp = null, ruleRegOp  = null, ruleMsiOp  = null;
                    if (card.Tag is object ruleObj)
                    {
                        var ruleType2 = ruleObj.GetType();
                        string rt2 = ruleType2.GetProperty("RuleType")?.GetValue(ruleObj)?.ToString() ?? "";
                        if (rt2.Equals("File",     StringComparison.OrdinalIgnoreCase)) ruleFileOp = ruleType2.GetProperty("FileOperator")?.GetValue(ruleObj)?.ToString();
                        if (rt2.Equals("Registry", StringComparison.OrdinalIgnoreCase)) ruleRegOp  = ruleType2.GetProperty("RegOperator")?.GetValue(ruleObj)?.ToString();
                        if (rt2.Equals("MSI",      StringComparison.OrdinalIgnoreCase)) ruleMsiOp  = ruleType2.GetProperty("MsiOperator")?.GetValue(ruleObj)?.ToString();
                    }

                    foreach (System.Windows.UIElement fieldChild in fields.Children)
                    {
                        if (fieldChild is not System.Windows.Controls.Grid opRow) continue;
                        if (opRow.Children.Count < 2) continue;

                        // Read the label in col 0 to identify the row
                        string rowLabel = "";
                        if (opRow.Children[0] is System.Windows.Controls.TextBlock lbl)
                            rowLabel = lbl.Text ?? "";

                        // Only process rows explicitly labelled "Operator"
                        if (!rowLabel.Equals("Operator", StringComparison.OrdinalIgnoreCase)) continue;

                        // Operator is either a standalone ComboBox (MSI) or inside a StackPanel (File/Registry)
                        if (opRow.Children[1] is System.Windows.Controls.ComboBox msiCmb)
                        {
                            ReplaceOperatorItems(msiCmb, intuneOps, ruleMsiOp);
                            continue;
                        }
                        if (opRow.Children[1] is not System.Windows.Controls.StackPanel opContent) continue;
                        if (opContent.Children.Count == 0) continue;
                        if (opContent.Children[0] is System.Windows.Controls.ComboBox opCmb)
                            ReplaceOperatorItems(opCmb, intuneOps, ruleFileOp ?? ruleRegOp);
                    }
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        private static bool IsOperatorComboBox(System.Windows.Controls.ComboBox cmb)
        {
            // Operator ComboBoxes contain items like "Equal to", "Greater than", "Equals", "More recent" etc.
            // Detection method ComboBoxes contain items like "Date created", "String (version)", "Key exists" etc.
            // Check first item to distinguish them.
            if (cmb.Items.Count == 0) return false;
            string? first = (cmb.Items[0] as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "";
            return first != null && (
                first.Contains("equal",   StringComparison.OrdinalIgnoreCase) ||
                first.Contains("than",    StringComparison.OrdinalIgnoreCase) ||
                first.Contains("recent",  StringComparison.OrdinalIgnoreCase) ||
                first.Contains("Older",   StringComparison.OrdinalIgnoreCase) ||
                first.Equals("Equals",    StringComparison.OrdinalIgnoreCase));
        }

        private static void ReplaceOperatorItems(System.Windows.Controls.ComboBox cmb, string[] intuneOps, string? overrideValue = null)
        {
            // Guard: only process operator ComboBoxes, not detection method ComboBoxes
            if (!IsOperatorComboBox(cmb)) return;

            // Always replace to ensure correct Intune list
            string selected = overrideValue
                ?? (cmb.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()
                ?? "";
            // Normalise old names to Intune names for matching
            var nameMap = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Equal to",               "Equals" },
                { "Not equal to",           "Not equal to" },
                { "Greater than or equal to","Greater than or equal to" },
                { "Greater than",           "Greater than" },
                { "Less than or equal to",  "Less than or equal to" },
                { "Less than",              "Less than" },
                { "More recent or equal to","Greater than or equal to" },
                { "More recent",            "Greater than" },
                { "Older or equal to",      "Less than or equal to" },
                { "Older",                  "Less than" },
            };
            if (nameMap.TryGetValue(selected, out string? mapped)) selected = mapped;
            cmb.Items.Clear();
            int selIdx = 0;
            for (int i = 0; i < intuneOps.Length; i++)
            {
                cmb.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = intuneOps[i] });
                if (intuneOps[i].Equals(selected, StringComparison.OrdinalIgnoreCase))
                    selIdx = i;
            }
            cmb.SelectedIndex = selIdx;
        }

        private void SplitDateTimeBoxesInPublishPage(dynamic publishPage)
        {
            try
            {
                if (publishPage?.FindName("DetectionRulesList") is not System.Windows.Controls.Panel list) return;
                foreach (System.Windows.UIElement child in list.Children)
                {
                    if (child is not System.Windows.Controls.Border card) continue;
                    if (card.Child is not System.Windows.Controls.Grid cardGrid) continue;
                    // Col 1 = fields StackPanel
                    if (cardGrid.Children.Count < 2) continue;
                    if (cardGrid.Children[1] is not System.Windows.Controls.StackPanel fields) continue;
                    // Find the Operator row — it's a Grid with [label][StackPanel(opCmb, valWrap, cb32)]
                    foreach (System.Windows.UIElement fieldChild in fields.Children)
                    {
                        if (fieldChild is not System.Windows.Controls.Grid opRow) continue;
                        if (opRow.Children.Count < 2) continue;
                        if (opRow.Children[1] is not System.Windows.Controls.StackPanel opContent) continue;
                        // opContent = [ComboBox(operator), Grid(valWrap), CheckBox(32bit)]
                        if (opContent.Children.Count < 2) continue;
                        if (opContent.Children[0] is not System.Windows.Controls.ComboBox opCmb) continue;
                        string opSel = (opCmb.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "";
                        // Only process if not already split (check if time box already added)
                        bool alreadySplit = opContent.Children.Count >= 3 &&
                            opContent.Children[2] is System.Windows.Controls.TextBox;
                        if (alreadySplit) continue;
                        if (opContent.Children[1] is not System.Windows.Controls.Grid valWrap) continue;
                        if (valWrap.Children.Count == 0) continue;
                        if (valWrap.Children[0] is not System.Windows.Controls.TextBox valBox) continue;
                        // Check if detection method is Date — look for dmCmb in fields
                        string detMethod = "";
                        foreach (System.Windows.UIElement fc in fields.Children)
                        {
                            if (fc is System.Windows.Controls.Grid dmGrid && dmGrid.Children.Count >= 2 &&
                                dmGrid.Children[1] is System.Windows.Controls.ComboBox dmCmb)
                            { detMethod = (dmCmb.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? ""; break; }
                        }
                        if (!detMethod.StartsWith("Date", StringComparison.OrdinalIgnoreCase)) continue;
                        // Split combined value "06/04/2026 12:00:00 AM" into date + time
                        string combined = valBox.Text ?? "";
                        string datePart = combined, timePart = "12:00:00 AM";
                        int sp = combined.IndexOf(' ');
                        if (sp > 0) { datePart = combined.Substring(0, sp).Trim(); timePart = combined.Substring(sp + 1).Trim(); }
                        valBox.Width = 130;
                        valBox.Text  = datePart;
                        // Build time TextBox
                        var timeBox = new System.Windows.Controls.TextBox
                        {
                            Text    = timePart,
                            Height  = 24, Width = 110, FontSize = 11,
                            Padding = new System.Windows.Thickness(6, 0, 6, 0),
                            Margin  = new System.Windows.Thickness(6, 0, 0, 0),
                            Style   = System.Windows.Application.Current.TryFindResource("ModernTextBoxStyle") as System.Windows.Style
                        };
                        timeBox.SetResourceReference(System.Windows.Controls.TextBox.BackgroundProperty, "InputBgBrush");
                        timeBox.SetResourceReference(System.Windows.Controls.TextBox.ForegroundProperty, "TextPrimaryBrush");
                        timeBox.SetResourceReference(System.Windows.Controls.TextBox.BorderBrushProperty, "BorderMainBrush");
                        // Keep valBox date text in sync
                        var capturedValBox  = valBox;
                        var capturedTimeBox = timeBox;
                        capturedTimeBox.TextChanged += (_, _) =>
                        {
                            // Update combined value when time changes
                            string d = capturedValBox.Text.Trim();
                            string t = capturedTimeBox.Text.Trim();
                            if (!string.IsNullOrWhiteSpace(d))
                                capturedValBox.Tag = d + (string.IsNullOrWhiteSpace(t) ? "" : " " + t);
                        };
                        // Insert time box after valWrap (before cb32)
                        int insertIdx = opContent.Children.IndexOf(valWrap) + 1;
                        opContent.Children.Insert(insertIdx, timeBox);
                    }
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        private void RemoveDetectionRuleRadioButtons(dynamic publishPage,
            bool wantMsi = true, bool wantFile = true, bool wantReg = true)
        {
            try
            {

                if (publishPage?.FindName("DetectionRulesList") is not System.Windows.Controls.Panel list) return;
                foreach (System.Windows.UIElement child in list.Children)
                {
                    if (child is not System.Windows.Controls.Border card) continue;
                    if (card.Child is not System.Windows.Controls.Grid cardGrid) continue;
                    if (cardGrid.Children.Count == 0) continue;
                    if (cardGrid.Children[0] is not System.Windows.Controls.Border typeBadge) continue;

                    // Get rule type text from existing child (StackPanel or TextBlock)
                    string ruleTypeText = "";
                    if (typeBadge.Child is System.Windows.Controls.StackPanel typeStack)
                    {
                        var lbl = typeStack.Children.OfType<System.Windows.Controls.TextBlock>().FirstOrDefault();
                        ruleTypeText = lbl?.Text ?? "";
                    }
                    else if (typeBadge.Child is System.Windows.Controls.TextBlock tb)
                        ruleTypeText = tb.Text;
                    else if (typeBadge.Child is System.Windows.Controls.CheckBox existingChk)
                    {
                        ruleTypeText = existingChk.Content?.ToString() ?? "";
                        existingChk.IsChecked = ruleTypeText.Equals("MSI",      StringComparison.OrdinalIgnoreCase) ? wantMsi
                                              : ruleTypeText.Equals("File",     StringComparison.OrdinalIgnoreCase) ? wantFile
                                              : ruleTypeText.Equals("Registry", StringComparison.OrdinalIgnoreCase) ? wantReg
                                              : true;
                        continue;
                    }

                    bool chkState = ruleTypeText.Equals("MSI",      StringComparison.OrdinalIgnoreCase) ? wantMsi
                                  : ruleTypeText.Equals("File",     StringComparison.OrdinalIgnoreCase) ? wantFile
                                  : ruleTypeText.Equals("Registry", StringComparison.OrdinalIgnoreCase) ? wantReg
                                  : true;

                    var chk = new System.Windows.Controls.CheckBox
                    {
                        Content           = ruleTypeText,
                        IsChecked         = chkState,
                        FontSize          = 11,
                        FontWeight        = System.Windows.FontWeights.SemiBold,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center,
                        Cursor            = System.Windows.Input.Cursors.Hand,
                    };
                    chk.SetResourceReference(System.Windows.Controls.CheckBox.ForegroundProperty, "TextPrimaryBrush");
                    typeBadge.Child = chk;
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        private void UpdateDetectionText()
        {
            // Detection controls removed from Package tab — method is a no-op
        }

        private void MsiVersionCheck_Changed(object sender, RoutedEventArgs e)
        {
        }

        // ══════════════════════════════════════════════════════════════════
        //  COMBO CHANGE HANDLERS
        // ══════════════════════════════════════════════════════════════════
        private void CmbCreateMst_Changed(object sender, RoutedEventArgs e)
        {
            // Only rebuild msiexec command string when no template folder is set.
            // When a template folder exists, commands are always Invoke-AppDeployToolkit.exe
            // and should not change just because the user toggles Create MST.
            string tpl = TxtTemplateFolder?.Text.Trim() ?? "";
            bool hasTemplate = !string.IsNullOrWhiteSpace(tpl) && Directory.Exists(tpl);
            if (!hasTemplate)
                RebuildCommands();

            // If MST is disabled, Remove Desktop Shortcut has no effect — force to No
            if (CmbRemoveDesktop != null && !ComboIsYes(CmbCreateMst))
                SetComboByContent(CmbRemoveDesktop, "No");
        }

        private void CmbInstallContext_Changed(object sender, RoutedEventArgs e)
            => UpdatePanelVisibility();

        private void CmbUninstallPkg_Changed(object sender, RoutedEventArgs e)
            => UpdatePanelVisibility();

        private void CmbRemoveDesktop_Changed(object sender, RoutedEventArgs e)
            => UpdatePanelVisibility();

        // ══════════════════════════════════════════════════════════════════
        //  CREATE  —  main orchestration
        // ══════════════════════════════════════════════════════════════════
        private async void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            // ── Route to EXE create if EXE tab active ────────────────────
            bool isExeTab = TabExe?.IsSelected == true;
            if (isExeTab) { await BtnCreate_Exe(); return; }

            // ── Validate source (MSI tab) ─────────────────────────────────
            string src = TxtSource.Text.Trim();
            if (string.IsNullOrWhiteSpace(src) ||
                (!File.Exists(src) && !Directory.Exists(src)))
            {
                ShowValidationDialog(
                    "Auto Package - Validation",
                    string.IsNullOrWhiteSpace(src)
                        ? "Source file or folder is required. Please browse or drag-and-drop an installer file or folder."
                        : $"Source path not found:\n{src}\n\nPlease browse to a valid file or folder.",
                    "⚠️");
                TxtSource.Focus();
                return;
            }
            TxtSourceError.Visibility = Visibility.Collapsed;

            string packageName = TxtPackageName.Text.Trim();
            if (string.IsNullOrWhiteSpace(packageName))
            {
                ShowValidationDialog(
                    "Auto Package - Validation",
                    "Package Name is empty. Please fill in the application info.",
                    "⚠️");
                return;
            }

            // ── Folder-exists: auto-increment revision if triggered from Source Download, else show dialog ──
            {
                string checkRoot = _cfg.ResolvedPackageOutputFolder;
                if (_triggeredFromSourceDownload)
                {
                    // Auto-increment App Revision until a free folder name is found
                    packageName = AutoIncrementRevisionUntilFree(checkRoot, isExeTab: false);
                }
                else
                {
                    string checkPath = IOPath.Combine(checkRoot, packageName);
                    if (Directory.Exists(checkPath))
                    {
                        bool overwrite = ShowFolderExistsDialog(checkPath);
                        if (!overwrite) return;
                    }
                }
                // Always reset the flag after consuming it
                _triggeredFromSourceDownload = false;
            }

            _busy = true;
            _createStartTime = DateTime.Now;
            _createCts = new System.Threading.CancellationTokenSource();
            _createPause.Set();   // ensure not paused from any previous run
            _creationPaused = false;
            var token = _createCts.Token;
            SetBusy(true);

            // ── Build paths ───────────────────────────────────────────────
            string rootBase       = _cfg.ResolvedPackageOutputFolder;
            // Re-read packageName after possible revision increment
            packageName = TxtPackageName?.Text.Trim() ?? packageName;
            string path1          = IOPath.Combine(rootBase, packageName);    // C:\Packages\VendorName_AppName_1.0_01
            string path2          = path1;                                     // template copied directly into path1
            string filesPath      = IOPath.Combine(path1, "Files");           // installer source → path1\Files
            string intunePath     = IOPath.Combine(path1, "Intune");
            _packageRootPath      = path1;

            // ── Log file ──────────────────────────────────────────────────
            string ts  = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _logPath   = IOPath.Combine(IOPath.GetTempPath(),
                             $"AutoPackage_{packageName}.log");
            // Start a fresh per-package log for this run (Package/Execute/Intunewin/Publish append to it).
            try { File.WriteAllText(_logPath, string.Empty); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            var log    = new StringBuilder();
            // Reset the on-screen EVENT LOG window for this run.
            try { if (PkgEventLog != null) { PkgEventLog.Clear(); } } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            void Log(string msg)
            {
                string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
                log.AppendLine(line);
                // Stream to the on-screen EVENT LOG window (cmd-style) in the Package tab.
                AppendPkgEventLog(line);
            }

            // ── Options snapshot ────────────────────────────────────────────
            AutoPackageAppEntry? perApp = _staticSelectedApp;
            bool doMst          = ComboIsYes(CmbCreateMst);
            bool doRemDesktop   = ComboIsYes(CmbRemoveDesktop);
            // Controls removed from Package tab — per-app JSON overrides global config
            string perAppCreateIntunewin = perApp?.CreateIntunewin?.Trim() ?? "";
            bool doIntunewin    = !string.IsNullOrWhiteSpace(perAppCreateIntunewin)
                                  ? perAppCreateIntunewin.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                                  : (_cfg?.CreateIntunewin ?? false);
            string perAppPublishToIntune = perApp?.PublishToIntune?.Trim() ?? "";
            string perAppPublishSettings = perApp?.PublishSettings?.Trim() ?? "";
            // PublishSettings=Yes → navigate to Publish tab + write Intune JSON (does not push to Intune).
            // PublishToIntune=Yes → same + also pushes to Intune.
            // Either flag enables the Publish tab and JSON write.
            bool doPublish      = (!string.IsNullOrWhiteSpace(perAppPublishToIntune)
                                  ? perAppPublishToIntune.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                                  : (_cfg?.PublishToIntune ?? false))
                               || (!string.IsNullOrWhiteSpace(perAppPublishSettings)
                                  ? perAppPublishSettings.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                                  : false);
            bool doDeploy       = !string.IsNullOrWhiteSpace(perApp?.AssignmentsRequired) ||
                                  !string.IsNullOrWhiteSpace(perApp?.AssignmentsAvailable) ||
                                  !string.IsNullOrWhiteSpace(perApp?.AssignmentsUninstall);
            // ── Publish decision log written immediately so it appears even if Create fails ──
            try
            {
                string publishDecisionLog = _logPath;  // unified per-package log (AutoPackage_{packageName}.log)
                var sb = new System.Text.StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] AutoPackage Publish Decision");
                sb.AppendLine($"  Package     : {packageName}");
                sb.AppendLine($"  doIntunewin : {doIntunewin}");
                sb.AppendLine($"  doDeploy    : {doDeploy}");
                sb.AppendLine($"  intunePath  : {intunePath}");
                sb.AppendLine($"  ClientId    : {(!string.IsNullOrEmpty(_cfg?.IntuneSettings?.ClientId) ? "[set]" : "[EMPTY - publish will fail]" )}");
                sb.AppendLine($"  TenantId    : {(!string.IsNullOrEmpty(_cfg?.IntuneSettings?.TenantId) ? "[set]" : "[EMPTY - publish will fail]" )}");
                File.AppendAllText(publishDecisionLog, sb.ToString());
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            bool doCreateDoc    = FindName("CmbCreateDocument") is ComboBox cmbCD2 && ComboIsYes(cmbCD2);
            string installBeh   = "System";
            // Read UninstallPackage from per-app JSON first, fall back to global config
            string perAppUninstallPkg = perApp?.UninstallPackage?.Trim() ?? "";
            bool doUninstallPkg = (!string.IsNullOrWhiteSpace(perAppUninstallPkg) && !perAppUninstallPkg.Equals("No", StringComparison.OrdinalIgnoreCase))
                                || (!string.IsNullOrWhiteSpace(_cfg?.UninstallPackage) && _cfg.UninstallPackage != "No");
            string uninstallPkgVal = doUninstallPkg
                ? (perAppUninstallPkg.Equals("System Context", StringComparison.OrdinalIgnoreCase) ? "System Context" : "Administrator")
                : "No";
            // Read InstallPackage from per-app JSON first, fall back to global config
            string perAppInstallPkg = perApp?.InstallPackage?.Trim() ?? "";
            bool doInstallPkg   = (!string.IsNullOrWhiteSpace(perAppInstallPkg) && !perAppInstallPkg.Equals("No", StringComparison.OrdinalIgnoreCase))
                                || (!string.IsNullOrWhiteSpace(_cfg?.InstallPackage) && _cfg.InstallPackage != "No");
            string installPkgVal  = doInstallPkg
                ? (perAppInstallPkg.Equals("System Context", StringComparison.OrdinalIgnoreCase) ? "System Context" : "Administrator")
                : "No";
            string msiCapDesc     = perApp?.Description ?? _cfg?.IntuneSettings?.Description ?? "";
            string groupName      = perApp?.AssignmentsAvailable ?? perApp?.AssignmentsRequired ?? "";
            string description    = msiCapDesc;
            string deploymentType = "";
            string closeApps    = TxtCloseApps.Text.Trim();

            // ── Always register post-create callback — overwrites MainWindow.SetAfterCreateCallback ──
            // This ensures AutoPackagePage's own Execute tab is used (not global Execute Package page)
            // and that the Intunewin→Publish chain fires in order after Execute completes.
            {
                bool captureInstall      = doInstallPkg;
                bool captureUninstall    = doUninstallPkg;
                // Guard: if the boolean flag is true but the combo value is still "No" (e.g. when
                // triggered from SourceDownload checkboxes which pass doInstallPkg=true but
                // _staticPendingInstallPackage="No"), fall back to "Administrator" so
                // NavigateToExecuteTab sees a non-"No" value and waits for Execute to complete.
                string captureInstallVal   = (captureInstall && installPkgVal.Equals("No", StringComparison.OrdinalIgnoreCase))
                                             ? "Administrator" : installPkgVal;
                string captureUninstallVal = (captureUninstall && uninstallPkgVal.Equals("No", StringComparison.OrdinalIgnoreCase))
                                             ? "Administrator" : uninstallPkgVal;
                _onAfterCreateComplete = () =>
                {
                    Action? afterExec = _pendingAfterExecuteChain;
                    _pendingAfterExecuteChain = null;

                    // ── Sequencing rule for CreateIntunewin=Yes ───────────────────────────
                    // When both Install and Uninstall package are requested AND CreateIntunewin
                    // is enabled, the correct order is:
                    //   Install Package → Uninstall Package → Intunewin tab
                    //
                    // OLD behaviour: deferUninstall=true caused Uninstall to be stored in
                    // _pendingAfterPublishUninstallExecute (after Publish), and only Install
                    // ran now — triggering Intunewin immediately after Install. WRONG.
                    //
                    // NEW behaviour:
                    //   • Install+Uninstall both run now (in one NavigateToExecuteTab call
                    //     using doInstall+doUninstall), with afterExec (intunewinThenPublish)
                    //     as onAfterExecute so Intunewin fires only after Uninstall completes.
                    //   • When only Uninstall (no Install) + Intunewin: same — Uninstall runs
                    //     now with afterExec as onAfterExecute.
                    //   • deferUninstall is retired for the Intunewin path entirely.
                    // ─────────────────────────────────────────────────────────────────────

                    if (captureInstall || captureUninstall)
                    {
                        string? ps1 = Directory.GetFiles(LastCreatedPackageFolder,
                            "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories).FirstOrDefault();

                        // When Publish (but NOT Intunewin) is active and uninstall runs without
                        // install, wire onAfterExecute to navigate to Publish after uninstall.
                        Action? publishNavAction = null;
                        if (_triggerPublishTab && !doIntunewin && captureUninstall)
                        {
                            string capturedPkgFolder    = LastCreatedPackageFolder;
                            string capturedPubAppName   = _publishCapAppName;
                            string capturedPubPublisher = _publishCapPublisher;
                            string capturedPubVersion   = _publishCapVersion;
                            string capturedPubDesc      = _publishCapDesc;
                            publishNavAction = () => Dispatcher.Invoke(() =>
                                NavigateToPublishTab(capturedPkgFolder,
                                    appName:    capturedPubAppName,
                                    appVendor:  capturedPubPublisher,
                                    appVersion: capturedPubVersion,
                                    description: capturedPubDesc));
                        }

                        // afterExec = intunewinThenPublish when doIntunewin=true, else publishNavAction
                        // or whatever the caller stored. Intunewin fires after Uninstall completes.
                        // When nothing downstream is configured (no Intunewin, no Publish) the executed
                        // step (Install/Uninstall package) is the terminus, so fall back to completing
                        // the flow here - otherwise onAfterExecute is null, the auto-flow lock is never
                        // released, and the app stays locked after a successful Install package.
                        Action? effectiveAfterExec = publishNavAction ?? afterExec
                            ?? (() => Dispatcher.Invoke(() => RaiseAutoFlowComplete()));

                        if (!string.IsNullOrWhiteSpace(ps1))
                        {
                            // When Publish is active AND both Install+Uninstall run,
                            // pre-fill the Publish tab right after Install (while app is
                            // still installed) so registry detection data is captured.
                            // The tab is not shown yet — it becomes visible only after
                            // Intunewin completes (via intunewinThenPublish / publishAction).
                            // When an Install runs, capture the app's uninstall-registry details
                            // right after it completes (while the app is still installed) so the
                            // Document tab's Display Name / Publisher / Version / Install Location
                            // survive the later Uninstall. The Publish-tab prefill additionally
                            // requires Uninstall + Intunewin/Publish to be part of the flow.
                            Action? preFillPublish = captureInstall
                                ? () => Dispatcher.Invoke(() =>
                                  {
                                      CaptureUninstallRegistryForDocument();
                                      if ((doIntunewin || _triggerPublishTab) && captureUninstall)
                                      {
                                          NavigateToPublishTab(LastCreatedPackageFolder,
                                              appName:    _publishCapAppName,
                                              appVendor:  _publishCapPublisher,
                                              appVersion: _publishCapVersion,
                                              description: _publishCapDesc,
                                              prefillOnly: true);
                                          // Stay on Execute tab so the user sees uninstall progress
                                          ActivateTopTab("Execute");
                                      }
                                  })
                                : null;

                            NavigateToExecuteTab(ps1,
                                installPackage:   captureInstall   ? captureInstallVal   : "No",
                                uninstallPackage: captureUninstall ? captureUninstallVal : "No",
                                onAfterExecute:   effectiveAfterExec,
                                onAfterInstall:   preFillPublish);
                        }
                        else
                        {
                            // MainWindow.NavigateToExecutePackage* — reflection to avoid compile-time dependency
                            _pendingAfterExecuteChain = effectiveAfterExec;
                            var win = Window.GetWindow(this);
                            if (win != null)
                            {
                                string mwMethod = (captureInstall && captureUninstall)
                                    ? "NavigateToExecutePackageAndInstallThenUninstall"
                                    : captureInstall
                                        ? "NavigateToExecutePackageAndInstall"
                                        : "NavigateToExecutePackageAndUninstall";
                                try { win.GetType().GetMethod(mwMethod)?.Invoke(win, new object[] { LastCreatedPackageFolder }); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                            }
                        }
                    }
                    else
                    {
                        if (afterExec != null)
                            afterExec.Invoke();
                        else
                            // Nothing downstream is configured (no Install/Uninstall package,
                            // no Intunewin, no Publish) — package creation is the terminus,
                            // so signal completion here to unlock the Download tab.
                            RaiseAutoFlowComplete();
                    }
                };
            }

            // ── Intunewin / Publish tab signals (independent of _onAfterCreateComplete) ──
            // Capture metadata now while UI controls are accessible.
            bool   msiTriggerIntunewin = doIntunewin;
            bool   msiTriggerPublish   = doPublish;
            string msiCapAppName    = TxtAppName?.Text.Trim()    ?? "";
            string msiCapPublisher  = TxtPublisher?.Text.Trim()  ?? "";
            string msiCapAppVersion = TxtAppVersion?.Text.Trim() ?? "";
            _triggerIntunewinTab = msiTriggerIntunewin;
            _triggerPublishTab   = msiTriggerPublish;
            _publishCapAppName   = msiCapAppName;
            _publishCapPublisher = msiCapPublisher;
            _publishCapVersion   = msiCapAppVersion;
            _publishCapDesc      = msiCapDesc;

            string productCode  = TxtProductCode.Text.Trim();
            string appVersion   = TxtAppVersion.Text.Trim();
            string msiFileName  = string.IsNullOrEmpty(_msiPath) ? "" : IOPath.GetFileName(_msiPath);
            string appName      = TxtAppName.Text.Trim();
            string appRevision  = string.IsNullOrWhiteSpace(TxtAppRevision?.Text.Trim()) ? "01" : TxtAppRevision!.Text.Trim();
            string publisher    = TxtPublisher.Text.Trim();
            string installCmd   = TxtInstallCmd.Text.Trim();
            string uninstallCmd = TxtUninstallCmd.Text.Trim();
            string templateFolder = TxtTemplateFolder.Text.Trim();
            string msiArgumentList  = TxtMsiArgumentList?.Text.Trim()  ?? "";
            string additionalArgs   = TxtAdditionalArgs?.Text.Trim()   ?? "";

            Log($"=== SwiftPack Auto Package - {packageName} ===");
            Log($"Config        : {ConfigPath}");
            Log($"Source (MSI)  : {src}");
            Log($"Source (EXE)  : {ExeTxtSource?.Text.Trim() ?? ""}");
            Log($"_msiPath      : {_msiPath}");
            Log($"_exePath      : {_exePath}");
            Log($"Tab selected  : {(TabExe?.IsSelected == true ? "EXE" : "MSI")}");
            Log($"Path1         : {path1}");
            Log($"Path2         : {path2}");


            try
            {
                bool msiCancelled = false;
                await Task.Run(() =>
                {
                    // Local helper: waits at pause gate, returns true if cancelled.
                    // Never throws — all cancellation signalled via msiCancelled flag.
                    bool Paused()
                    {
                        // Wait with a spin-poll so we never block on the token directly
                        while (_createPause.IsSet == false)
                        {
                            if (token.IsCancellationRequested) { msiCancelled = true; return true; }
                            System.Threading.Thread.Sleep(100);
                        }
                        if (token.IsCancellationRequested) { msiCancelled = true; return true; }
                        return false;
                    }

                    // ── STEP 1: Create package root folder ───────────────
                    Log("Step 1/12 - Creating package folder…");
                    bool hasTemplate = !string.IsNullOrWhiteSpace(templateFolder) && Directory.Exists(templateFolder);
                    Directory.CreateDirectory(path1);
                    SetProgress(10);
                    if (Paused()) return;

                    // ── STEP 2: Copy template ─────────────────────────────
                    // Position the copied template so the folder containing
                    // Invoke-AppDeployToolkit.ps1 sits ONE level below the package root:
                    //   • ps1 at template root        → wrap into <path1>\Package
                    //   • ps1 in a template subfolder → copy as-is (structure preserved,
                    //                                    e.g. AppPackage or Output\AppPackage)
                    // Intune / Documents are then created beside that folder (its parent).
                    Log("Step 2/12 - Copying template…");
                    string templateCopyDest = path1;
                    if (hasTemplate)
                    {
                        string? tplPs1 = FindDeploymentPs1(templateFolder);
                        string tplPs1Dir = tplPs1 != null ? (IOPath.GetDirectoryName(tplPs1) ?? templateFolder) : templateFolder;
                        bool ps1AtTemplateRoot = tplPs1 != null && SamePath(tplPs1Dir, templateFolder);
                        if (ps1AtTemplateRoot)
                            templateCopyDest = IOPath.Combine(path1, "Package");
                        Directory.CreateDirectory(templateCopyDest);
                        CopyDirectory(templateFolder, templateCopyDest);
                        Log($"  Template copied: {templateFolder} → {templateCopyDest}");
                    }
                    else Log("  No template folder - skipped.");

                    // Resolve the output base (parent of the ps1's folder) and place the
                    // Intune folder there so it is a sibling of the app-package folder.
                    string outputBase = ResolveOutputBase(path1);
                    intunePath = IOPath.Combine(outputBase, "Intune");
                    if (doIntunewin) Directory.CreateDirectory(intunePath);
                    Log($"  Output base   : {outputBase}");
                    Log($"  Intune folder : {intunePath}");
                    SetProgress(20);
                    if (Paused()) return;

                    // ── STEP 2b: Clear Files\ contents from template (keep the folder itself) ──
                    // Find Files\ as the sibling of Invoke-AppDeployToolkit.ps1.
                    // Wipe any stale files the template left inside it.
                    string ResolveFilesFolder()
                    {
                        string? ps1 = Directory.GetFiles(path1, "Invoke-AppDeployToolkit.ps1",
                                          SearchOption.AllDirectories).FirstOrDefault();
                        if (ps1 != null)
                        {
                            string sibling = IOPath.Combine(IOPath.GetDirectoryName(ps1) ?? path1, "Files");
                            if (!Directory.Exists(sibling)) Directory.CreateDirectory(sibling);
                            return sibling;
                        }
                        string fallback = IOPath.Combine(path1, "Files");
                        Directory.CreateDirectory(fallback);
                        return fallback;
                    }
                    string resolvedFilesFolder = hasTemplate ? ResolveFilesFolder() : IOPath.Combine(path1, "Files");
                    filesPath = resolvedFilesFolder; // update filesPath to the discovered Files folder
                    if (hasTemplate && Directory.Exists(filesPath))
                    {
                        Log("  Clearing Files\\ contents from template…");
                        foreach (string ff in Directory.GetFiles(filesPath, "*", SearchOption.AllDirectories))
                            try { File.Delete(ff); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        foreach (string dd in Directory.GetDirectories(filesPath).OrderByDescending(x => x.Length))
                            try { Directory.Delete(dd, recursive: true); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        Log("  Files\\ cleared.");
                    }

                    // ── STEP 3: Copy source into Files\ ─────────────────
                    string copyDest = hasTemplate ? filesPath : path1;
                    Log($"Step 3/12 - Copying source → {copyDest}");
                    if (Directory.Exists(src))
                    {
                        Directory.CreateDirectory(copyDest);
                        CopyDirectory(src, copyDest);
                        Log($"  Copied folder contents from {src}");
                    }
                    else if (File.Exists(src))
                    {
                        Directory.CreateDirectory(copyDest);
                        string dest = IOPath.Combine(copyDest, IOPath.GetFileName(src));
                        File.Copy(src, dest, overwrite: true);
                        Log($"  Copied: {IOPath.GetFileName(src)}");
                    }
                    SetProgress(30);
                    if (Paused()) return;

                    // ── Resolve MSI ──────────────────────────────────────
                    string localMsi = Directory.GetFiles(path2, "*.msi",
                                          SearchOption.AllDirectories).FirstOrDefault() ?? _msiPath;
                    string localMsiName = string.IsNullOrEmpty(localMsi) ? msiFileName
                                              : IOPath.GetFileName(localMsi);

                    // ── STEP 4: Create MST ───────────────────────────────
                    string mstPath = "";
                    if (doMst && !string.IsNullOrEmpty(localMsi) && File.Exists(localMsi))
                    {
                        Log("Step 4/12 - Creating MST…");
                        mstPath = IOPath.ChangeExtension(localMsi, ".mst");

                        // The transform's content, in priority order:
                        //   1. the global MST Properties from Settings (AutoPackage.json) -
                        //      each applied as an upsert: INSERT when the property does not
                        //      exist in this MSI, UPDATE to the configured value when it does;
                        //   2. the app's own ARP flags - AddNoModify / AddNoRepair /
                        //      AddNoRemove map to ARPNOMODIFY / ARPNOREPAIR / ARPNOREMOVE,
                        //      the same intent the PSADT script enforces post-install via
                        //      the registry, applied here at install time as well. Skipped
                        //      when the Settings list already carries the same name.
                        // No synthetic marker property is added (it was, once, and was
                        // rejected): if every configured value happens to already be in the
                        // MSI verbatim, GenerateTransform has nothing to record and the run
                        // logs MST_NODIFF instead of writing an empty file - Windows
                        // Installer offers no way to produce a transform with no changes.
                        var mstProps = new List<AutoMstProperty>(_cfg.MSTProperties ?? new List<AutoMstProperty>());
                        void AddIfMissing(string name, string value)
                        {
                            if (!mstProps.Any(mp => mp != null &&
                                    string.Equals(mp.Property?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                                mstProps.Add(new AutoMstProperty { Property = name, Value = value });
                        }
                        if (_cfg.AddNoModify) AddIfMissing("ARPNOMODIFY", "1");
                        if (_cfg.AddNoRepair) AddIfMissing("ARPNOREPAIR", "1");
                        if (_cfg.AddNoRemove) AddIfMissing("ARPNOREMOVE", "1");

                        CreateMstViaPs(localMsi, mstPath, mstProps, doRemDesktop, log);
                        if (File.Exists(mstPath))
                            Log($"  MST created: {IOPath.GetFileName(mstPath)}");
                        else
                        {
                            Log("  ⚠ MST not created - see the PS lines above.");
                            mstPath = "";   // nothing exists at this path; later steps must not reference it
                        }
                    }
                    else Log("Step 4/12 - Create MST skipped.");
                    SetProgress(46);
                    if (Paused()) return;

                    // ── STEP 5: Desktop shortcut ─────────────────────────
                    Log($"Step 5/12 - Desktop shortcut: {(doRemDesktop && !string.IsNullOrEmpty(mstPath) ? "handled in MST" : "skipped")}.");
                    SetProgress(50);

                    // ── STEP 6: Patch Invoke-AppDeployToolkit.ps1 ────────
                    // Copy from PackageTemplateFolder ONLY if TemplateFolder was set in the app JSON.
                    bool hasTemplateFolder = Dispatcher.Invoke(() => !string.IsNullOrWhiteSpace(TxtTemplateFolder?.Text?.Trim()));
                    if (_appTemplateFolderEnabled && !string.IsNullOrWhiteSpace(_pendingTemplateFolder))
                    {
                        string srcPs1 = IOPath.Combine(_pendingTemplateFolder, "Invoke-AppDeployToolkit.ps1");
                        if (File.Exists(srcPs1))
                        {
                            string? existingPs1 = Directory.GetFiles(path2, "Invoke-AppDeployToolkit.ps1",
                                                      SearchOption.AllDirectories).FirstOrDefault();
                            string destPs1 = existingPs1 ?? IOPath.Combine(path2, "Invoke-AppDeployToolkit.ps1");
                            Directory.CreateDirectory(IOPath.GetDirectoryName(destPs1)!);
                            File.Copy(srcPs1, destPs1, overwrite: true);
                            Log($"  PackageTemplateFolder Invoke-AppDeployToolkit.ps1 copied → {destPs1}");
                        }
                        else Log($"  TemplateFolder set but Invoke-AppDeployToolkit.ps1 not found in {_pendingTemplateFolder} - skipped.");
                    }
                    Log("Step 6/12 - Patching Invoke-AppDeployToolkit.ps1…");
                    string? invokeScript = Directory.GetFiles(path2,
                        "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (invokeScript != null)
                    {
                        PatchInvokeScript(invokeScript, localMsiName,
                            IOPath.GetFileName(mstPath), productCode, closeApps, log,
                            appName: appName, appVendor: publisher, appVersion: appVersion,
                            appRevision: appRevision, msiArgumentList: msiArgumentList,
                            additionalArgs: additionalArgs,
                            addMsi: _cfg.AddMsiCommandToPs1, addMst: _cfg.AddMstCommandToPs1,
                            addMsp: _cfg.AddMspCommandToPs1, addExe: _cfg.AddExeCommandToPs1,
                            addBat: _cfg.AddBatCommandToPs1, addIss: _cfg.AddIssCommandToPs1,
                            addReg: _cfg.AddRegCommandToPs1, addCer: _cfg.AddCerCommandToPs1,
                            addInf: _cfg.AddInfCommandToPs1, cfg: _cfg);
                    }
                    else Log("  Invoke-AppDeployToolkit.ps1 not found - skipped.");
                    // Copy Files\ from PackageTemplateFolder AFTER patching.
                    // Runs whether or not PSADTTemplateFolder is set.
                    if (!string.IsNullOrWhiteSpace(_pendingTemplateFolder))
                    {
                        string srcFiles = IOPath.Combine(_pendingTemplateFolder, "Files");
                        if (Directory.Exists(srcFiles))
                        {
                            if (!Directory.Exists(filesPath)) Directory.CreateDirectory(filesPath);
                            CopyDirectory(srcFiles, filesPath);
                            Log($"  PackageTemplateFolder Files\\ copied (post-patch): {srcFiles} → {filesPath}");
                        }
                        if (_appTemplateFolderEnabled)
                        {
                            _pendingTemplateFolder    = "";
                            _appTemplateFolderEnabled = false;
                        }
                    }
                    SetProgress(60);
                    if (Paused()) return;

                    // ── STEP 7: Create intunewin — skipped here, handled by Intunewin tab ──
                    // The .intunewin is created by NavigateToIntunewinTabAndCreate after Execute
                    // completes (or directly after Package if Execute is not needed).
                    // ── STEP 7: Find .intunewin from Intune subfolder ────────
                    Log($"Step 7/12 - Locating .intunewin file (doPublish={doPublish}, intunePath={intunePath})…");
                    string intunewinFile = "";
                    if (doPublish && !Directory.Exists(intunePath))
                        Log($"  ⚠ Intune folder not found: {intunePath} - publish skipped.");
                    if (doPublish && Directory.Exists(intunePath))
                    {
                        // Primary: <intunePath>\<packageName>.intunewin
                        string expectedIntunewin = IOPath.Combine(intunePath, packageName + ".intunewin");
                        if (File.Exists(expectedIntunewin))
                            intunewinFile = expectedIntunewin;
                        else
                        {
                            // Fallback: first .intunewin found in Intune folder
                            intunewinFile = Directory.GetFiles(intunePath, "*.intunewin")
                                                     .FirstOrDefault() ?? "";
                        }
                        if (!string.IsNullOrEmpty(intunewinFile))
                            Log($"  Found: {intunewinFile}");
                        else
                            Log("  ⚠ No .intunewin file found in Intune folder - publish will be skipped.");
                    }
                    SetProgress(72);
                    if (token.IsCancellationRequested) { msiCancelled = true; return; }

                    // ── STEP 8: Publish to Intune — via AutoPackage_PublishToIntune.ps1
                    string appId2 = "";
                    Log("Step 8/12 - Publish to Intune: will be triggered automatically after uninstall via Publish tab.");
                    SetProgress(86);

                    Log("Step 9/12 - Assign to group skipped (handled by AutoPackage_PublishToIntune.ps1).");
                    SetProgress(94);

                    // ── Package tab field summary (MSI flow) ──────────────────
                    Log("--- Package Fields ---");
                    Log($"AppName              : {appName}");
                    Log($"AppVendor            : {publisher}");
                    Log($"AppVersion           : {appVersion}");
                    Log($"AppRevision          : {appRevision}");
                    Log($"AppLang              : EN");
                    Log($"PackageName          : {packageName}");
                    Log($"SourcePath           : {src}");
                    Log($"UninstallPath        : ");
                    Log($"InstallArgs          : {msiArgumentList}");
                    Log($"UninstallArgs        : {additionalArgs}");
                    Log($"InstallCommand       : {installCmd}");
                    Log($"UninstallCommand     : {uninstallCmd}");

                    // Req: with no PSADT template the Intune Install/Uninstall command IS the source
                    // command computed here (not Invoke-AppDeployToolkit.exe). Persisted via the
                    // per-app save that copies _staticSelectedApp, and used by Publish through it.
                    if (_staticSelectedApp != null && string.IsNullOrWhiteSpace(_staticSelectedApp.PSADTTemplateFolder))
                    {
                        _staticSelectedApp.InstallCommand   = installCmd;
                        _staticSelectedApp.UninstallCommand = uninstallCmd;
                    }
                    Log($"CloseApplications    : {closeApps}");
                    Log($"GroupName            : {groupName}");
                    Log($"ProductCode          : {productCode}");
                    // ── Detection fields (read on UI thread) ──────────────────
                    bool msiIsDetMsi  = false;
                    bool msiIsDetFile = false;
                    bool msiIsDetReg  = false;
                    bool msiVersionYes= false;
                    string msiDetFPath = "";
                    string msiDetFName = "";
                    string msiDetFVer  = "";
                    string msiDetFMeth = "";
                    string msiDetFOp   = "";
                    string msiDetRKey  = "";
                    string msiDetRVal  = "";
                    string msiDetRVer  = "";
                    string msiDetRMeth = "";
                    string msiDetROp   = "";
                    string msiVerValue = "";
                    string msiVerOp    = "";
                    // MSI detection block
                    if (msiIsDetMsi || !string.IsNullOrEmpty(productCode))
                    {
                        Log($"DetectionType          : MSI");
                        Log($"Detection.MSI.ProductCode : {productCode}");
                        if (msiVersionYes)
                        {
                            Log($"Detection.MSI.Operator : {msiVerOp}");
                            Log($"Detection.MSI.Version  : {msiVerValue}");
                        }
                    }
                    // File detection block
                    if (msiIsDetFile || !string.IsNullOrEmpty(msiDetFPath) || !string.IsNullOrEmpty(msiDetFName))
                    {
                        Log($"DetectionType          : File");
                        Log($"Detection.File.Path    : {msiDetFPath}");
                        Log($"Detection.File.Name    : {msiDetFName}");
                        Log($"Detection.File.Method  : {msiDetFMeth}");
                        Log($"Detection.File.Operator: {msiDetFOp}");
                        Log($"Detection.File.Version : {msiDetFVer}");
                    }
                    // Registry detection block
                    if (msiIsDetReg || !string.IsNullOrEmpty(msiDetRKey) || !string.IsNullOrEmpty(msiDetRVal))
                    {
                        Log($"DetectionType          : Registry");
                        Log($"Detection.Reg.KeyPath  : {msiDetRKey}");
                        Log($"Detection.Reg.ValueName: {msiDetRVal}");
                        Log($"Detection.Reg.Method   : {msiDetRMeth}");
                        Log($"Detection.Reg.Operator : {msiDetROp}");
                        Log($"Detection.Reg.Version  : {msiDetRVer}");
                    }
                    Log("--- End Package Fields ---");

                    File.AppendAllText(_logPath, log.ToString());
                    SetProgress(100);

                    Dispatcher.Invoke(() =>
                    {
                        SetBusy(false);
                        _busy = false;
                        AutoFlowStatus.Set(AutoFlowStatus.CreatePackage, AutoFlowStatus.State.Ok);
                        ShowSuccessDialog(packageName, path1, _logPath, _createStartTime);
                    });
                });

                // Show cancellation message on UI thread after Task.Run completes cleanly
                if (msiCancelled)
                {
                    try { File.AppendAllText(_logPath, log.ToString()); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                }
            }
            catch (Exception ex)
            {
                Log($"❌ Fatal error: {ex.Message}");
                try { File.AppendAllText(_logPath, log.ToString()); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                try { Dispatcher.Invoke(() => ShowValidationDialog("Auto Package - Error", $"Auto Package failed:\n\n{ex.Message}\n\nSee log for details.", "⛔")); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                // Mark Create Package failed and abort the chain: release the auto-flow lock so the
                // Download/Package columns and top tabs reactivate, and paint the failed status -
                // without running any downstream step (Uninstall source / Install package / etc.).
                AutoFlowStatus.Set(AutoFlowStatus.CreatePackage, AutoFlowStatus.State.Failed);
                try { Dispatcher.Invoke(() => AbortAutoFlow()); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            finally
            {
                _createCts?.Dispose(); _createCts = null;
                _activeIntuneProc = null;
                _creationPaused = false;
                _createPause.Set();
                if (_busy) { Dispatcher.Invoke(() => { SetBusy(false); _busy = false; }); }
            }
        }

        private void SetProgress(int val)
            => Dispatcher.BeginInvoke(new Action(() => PrgProgress_Ctrl.Value = val));

        /// <summary>
        /// Disable all interactive controls while creating; re-enable when done.
        /// Cancel is the inverse — enabled ONLY during creation.
        /// Must be called on the UI thread.
        /// </summary>
        // ── View-lock for the Package tab (Download tab stays fully editable) ──
        private bool _pkgViewLockApplied;
        private void ApplyPackageViewLock()
        {
            if (_pkgViewLockApplied) return;
            _pkgViewLockApplied = true;

            // Lock ONLY the Package tab content (MSI + EXE field layouts).
            // Do NOT lock FrameDownload (Download tab must remain editable) or the
            // Execute/Intunewin/Publish frames (those pages lock themselves).
            AutoViewLock.LockInputs(PkgContentCard);

            // Hide manual toolbar buttons (MSI/header scope).
            AutoViewLock.Hide(HdrBtnBrowseFile);
            AutoViewLock.Hide(HdrBtnBrowseFolder);
            AutoViewLock.Hide(HdrBtnCancelSource);
            AutoViewLock.Hide(BtnRefresh_Ctrl);
            AutoViewLock.Hide(BtnClearAll_Ctrl);
            AutoViewLock.Hide(BtnBrowseTemplate);
            AutoViewLock.Hide(BtnClearTemplate);

            // Hide EXE-layout manual buttons.
            AutoViewLock.Hide(ExeBtnBrowseFile);
            AutoViewLock.Hide(ExeBtnBrowseFolder);
            AutoViewLock.Hide(ExeBtnCancelSource);
            AutoViewLock.Hide(ExeBtnBrowseUninstallFile);
            AutoViewLock.Hide(ExeBtnCancelUninstallSource);
            AutoViewLock.Hide(ExeBtnBrowseTemplate);
            AutoViewLock.Hide(ExeBtnClearTemplate);

            // Hide every inline per-field ✕ clear button inside the Package content
            // (they have no x:Name; find them by their FieldClear_Click handler is not
            // possible from here, so collapse the small clear buttons by walking the
            // Package content tree and matching their ✕ content + Click target).
            HideInlineClearButtons(PkgContentCard);

            // Keep Create — greyed look only (its IsEnabled is data-driven via SetBusy
            // and required for the automation's RaiseEvent). Cancel stays active
            // (SetBusy enables it only during creation).
            BtnCreate_Ctrl.Visibility = Visibility.Collapsed;   // hidden from the user; automation still fires it via RaiseEvent (IsEnabled stays data-driven via SetBusy)
        }

        // Collapse the tiny inline "✕" clear buttons (per-field) within a subtree.
        private static void HideInlineClearButtons(DependencyObject? root)
        {
            if (root == null) return;

            if (root is Button rb0 && rb0.Content is string rs0 && rs0.Trim() == "✕")
                rb0.Visibility = Visibility.Collapsed;

            // VisualTreeHelper only works on Visual / Visual3D. Logical children can
            // include non-visual nodes (e.g. ColumnDefinition, RowDefinition) that
            // throw if passed to GetChildrenCount — so guard the visual walk.
            if (root is System.Windows.Media.Visual || root is System.Windows.Media.Media3D.Visual3D)
            {
                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < n; i++)
                {
                    var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                    HideInlineClearButtons(c);
                }
            }

            // Also walk logical children (covers not-yet-rendered items).
            foreach (var lc in LogicalTreeHelper.GetChildren(root))
                if (lc is DependencyObject d)
                    HideInlineClearButtons(d);
        }

        private void SetBusy(bool busy)
        {
            // ── Toolbar action buttons (direct _Ctrl accessors via FindName on toolbar scope) ──
            BtnCreate_Ctrl.IsEnabled   = !busy;
            BtnRefresh_Ctrl.IsEnabled  = !busy;
            BtnClearAll_Ctrl.IsEnabled = !busy;
            BtnCancel_Ctrl.IsEnabled   = busy;   // Cancel is only active during creation

            // ── Header source browse buttons (compiled x:Name fields) ──
            HdrBtnBrowseFile.IsEnabled   = !busy;
            HdrBtnBrowseFolder.IsEnabled = !busy;
            HdrBtnCancelSource.IsEnabled = !busy;

            // ── Uninstall Source browse (EXE tab — compiled fields) ──
            if (ExeBtnBrowseUninstallFile   != null) ExeBtnBrowseUninstallFile.IsEnabled   = !busy;
            if (ExeBtnCancelUninstallSource != null) ExeBtnCancelUninstallSource.IsEnabled = !busy;

            // ── Template Folder browse (MSI tab — compiled fields) ──
            if (BtnBrowseTemplate != null) BtnBrowseTemplate.IsEnabled = !busy;
            if (BtnClearTemplate  != null) BtnClearTemplate.IsEnabled  = !busy;

            // ── Template Folder browse (EXE tab — compiled fields) ──
            if (ExeBtnBrowseTemplate != null) ExeBtnBrowseTemplate.IsEnabled = !busy;
            if (ExeBtnClearTemplate  != null) ExeBtnClearTemplate.IsEnabled  = !busy;

            // ── Disable/enable all textboxes during busy ──
            SetAllTextBoxesReadOnly(this, busy);

            // ── Progress bar ──
            PrgProgress_Ctrl.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            TxtStatus.Text = "";
        }

        private static void SetAllTextBoxesReadOnly(System.Windows.DependencyObject parent, bool readOnly)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is TextBox tb && !tb.IsReadOnly.Equals(readOnly))
                    tb.IsReadOnly = readOnly;
                else
                    SetAllTextBoxesReadOnly(child, readOnly);
            }
        }

        // ── Cancel button handler ───────────────────────────────────────────────
        // Clicking Cancel during package creation:
        //   1. Immediately pauses the background work (via _createPause.Reset()).
        //   2. If a .intunewin process is running, suspends it at the OS level.
        //   3. Shows a Yes/No confirmation dialog.
        //      • Yes  → cancels the creation.
        //      • No   → resumes work and continues.
        //      • (no action) → work stays paused until user responds.
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_createCts == null || _createCts.IsCancellationRequested) return;
            if (_creationPaused) return; // dialog already open

            _creationPaused = true;

            // 1. Pause the background Task at the next _createPause.Wait() checkpoint.
            //    If IntuneWinAppUtil is currently running its own poll loop (not at a
            //    checkpoint), that loop will detect the cancelled token within 200 ms.
            _createPause.Reset();

            // 2. If IntuneWinAppUtil is running, suspend it at OS level so no more
            //    disk I/O happens while the dialog is open.
            var intuneProc = _activeIntuneProc;
            if (intuneProc != null && !intuneProc.HasExited)
                SuspendProcess(intuneProc);

            // 3. Show the confirmation dialog (blocks UI thread modally).
            bool confirmed = ShowCancelCreationDialog();

            if (confirmed)
            {
                // User chose Yes — signal cancellation.
                // The RunIntuneWinAppUtil poll loop will detect IsCancellationRequested
                // and kill the process itself; we just need to resume it from suspension
                // first so it can actually exit.
                if (intuneProc != null && !intuneProc.HasExited)
                    ResumeProcess(intuneProc);   // un-suspend so the poll loop can kill it
                _createCts.Cancel();             // signal token - poll loop kills & returns ""
                _creationPaused = false;
                // Leave _createPause Reset — the finally block will Set() it on cleanup.
            }
            else
            {
                // User chose No — resume everything.
                _creationPaused = false;
                if (intuneProc != null && !intuneProc.HasExited)
                    ResumeProcess(intuneProc);
                _createPause.Set();
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  VALIDATION MESSAGE DIALOG  (standard themed template)
        // ══════════════════════════════════════════════════════════════════
        private void ShowValidationDialog(string title, string message, string icon = "⚠️")
        {
            var win = new Window
            {
                Title                 = title,
                Width                 = 460,
                MaxWidth              = 460,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = Window.GetWindow(this),
                ResizeMode            = ResizeMode.NoResize,
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = System.Windows.Media.Brushes.Transparent,
                FontFamily            = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar         = false,
            };

            var winBg     = Application.Current.TryFindResource("WindowBgBrush")   as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
            var borderClr = Application.Current.TryFindResource("BorderMainBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
            var outerBorder = new Border
            {
                Background      = winBg,
                BorderBrush     = borderClr,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10),
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18,
                    Color = System.Windows.Media.Colors.Black
                }
            };
            win.Content = outerBorder;

            var outerGrid = new Grid();
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerBorder.Child = outerGrid;

            // ── Title bar ────────────────────────────────────────────────
            var titleBarBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));
            var titleBar = new Border
            {
                Background      = titleBarBg,
                BorderBrush     = borderClr,
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius    = new CornerRadius(10, 10, 0, 0),
                Padding         = new Thickness(16, 6, 12, 6)
            };
            titleBar.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(titleBar, 0);

            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleIcon = new TextBlock { Text = "⚡", FontSize = 14, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var titleText = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            var closeBtn  = new Button   { Content = "✕", FontSize = 11, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            closeBtn.Click += (_, _) => win.Close();
            Grid.SetColumn(titleIcon, 0); Grid.SetColumn(titleText, 1); Grid.SetColumn(closeBtn, 2);
            titleGrid.Children.Add(titleIcon); titleGrid.Children.Add(titleText); titleGrid.Children.Add(closeBtn);
            titleBar.Child = titleGrid;
            outerGrid.Children.Add(titleBar);

            // ── Content ──────────────────────────────────────────────────
            var root = new Grid { Margin = new Thickness(8) };
            Grid.SetRow(root, 1);
            outerGrid.Children.Add(root);

            var boxBg  = Application.Current.TryFindResource("InputBgBrush")       as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF1, 0xF8));
            var boxBdr = Application.Current.TryFindResource("BorderMainBrush")    as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg  = Application.Current.TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x5C, 0x7A));
            var valFg  = Application.Current.TryFindResource("TextPrimaryBrush")   as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1D, 0x2E));

            var details = new Grid();
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Row 0 — message box
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var msgLbl = new TextBlock { Text = "Message", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
            var msgBox = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6) };
            msgBox.Child = new TextBlock { Text = message, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = valFg };
            Grid.SetRow(msgLbl, 0); Grid.SetColumn(msgLbl, 0);
            Grid.SetRow(msgBox, 0); Grid.SetColumn(msgBox, 1);
            details.Children.Add(msgLbl); details.Children.Add(msgBox);

            // Row 1 — spacer
            details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            // Row 2 — OK button
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Style? sec = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;
            var iconTb = ApDecisionIcon();
            Grid.SetRow(iconTb, 2); Grid.SetColumn(iconTb, 0);
            details.Children.Add(iconTb);

            var okBtn = new Button { Content = "✔ OK", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6, 0, 6, 0), IsDefault = true };
            ApApplyPrimaryLook(okBtn);
            okBtn.Click += (_, _) => win.Close();
            Grid.SetRow(okBtn, 2); Grid.SetColumn(okBtn, 1);
            details.Children.Add(okBtn);

            root.Children.Add(details);
            win.ShowDialog();
        }

        // Give a dialog button the highlighted "primary action" look: a filled
        // accent background, rounded corners and a subtle hover. Theme resources
        // keep it readable in both light and dark modes, mirroring the Publish
        // Package dialog. Applied on top of any existing Style (local values win
        // in WPF), so callers keep their own sizing.
        private static void ApApplyPrimaryLook(Button b)
        {
            System.Windows.Media.Brush bg     = Application.Current.TryFindResource("DlgBtnBlueBgBrush")     as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x9B, 0xF5));
            System.Windows.Media.Brush fg     = Application.Current.TryFindResource("DlgBtnBlueFgBrush")     as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
            System.Windows.Media.Brush border = Application.Current.TryFindResource("DlgBtnBlueBorderBrush") as System.Windows.Media.Brush ?? bg;

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
        private static TextBlock ApDecisionIcon()
            => new TextBlock
            {
                Text = "\u2753",
                FontSize = 22,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x9B, 0xF5)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center
            };

        private bool ShowCancelCreationDialog()
        {
            bool result = false;

            var win = new Window
            {
                Title                 = "Auto Package - Cancel Creation",
                Width                 = 460,
                MaxWidth              = 460,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = Window.GetWindow(this),
                ResizeMode            = ResizeMode.NoResize,
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = System.Windows.Media.Brushes.Transparent,
                FontFamily            = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar         = false,
            };

            System.Windows.Media.Brush Res(string key) =>
                (System.Windows.Media.Brush?)Application.Current.TryFindResource(key)
                ?? System.Windows.Media.Brushes.Gray;

            var winBg     = Application.Current.TryFindResource("WindowBgBrush")   as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
            var borderClr = Application.Current.TryFindResource("BorderMainBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
            var outerBorder = new Border
            {
                Background      = winBg,
                BorderBrush     = borderClr,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10),
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18,
                    Color = System.Windows.Media.Colors.Black
                }
            };
            win.Content = outerBorder;

            var outerGrid = new Grid();
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerBorder.Child = outerGrid;

            // ── Title bar ────────────────────────────────────────────────
            var titleBarBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));
            var titleBar = new Border
            {
                Background      = titleBarBg,
                BorderBrush     = borderClr,
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius    = new CornerRadius(10, 10, 0, 0),
                Padding         = new Thickness(16, 6, 12, 6)
            };
            titleBar.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(titleBar, 0);

            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleIcon = new TextBlock { Text = "⚡", FontSize = 14, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var titleText = new TextBlock { Text = "Auto Package - Cancel Creation", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            var closeBtn  = new Button   { Content = "✕", FontSize = 11, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            closeBtn.Click += (_, _) => win.Close();
            Grid.SetColumn(titleIcon, 0); Grid.SetColumn(titleText, 1); Grid.SetColumn(closeBtn, 2);
            titleGrid.Children.Add(titleIcon); titleGrid.Children.Add(titleText); titleGrid.Children.Add(closeBtn);
            titleBar.Child = titleGrid;
            outerGrid.Children.Add(titleBar);

            // ── Content ──────────────────────────────────────────────────
            var root = new Grid { Margin = new Thickness(8) };
            Grid.SetRow(root, 1);
            outerGrid.Children.Add(root);

            var boxBg  = Application.Current.TryFindResource("InputBgBrush")       as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF1, 0xF8));
            var boxBdr = Application.Current.TryFindResource("BorderMainBrush")    as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg  = Application.Current.TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x5C, 0x7A));
            var valFg  = Application.Current.TryFindResource("TextPrimaryBrush")   as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1D, 0x2E));

            var details = new Grid();
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Row 0 — Status box
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var statusLbl = new TextBlock { Text = "Status", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
            var statusBox = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6) };
            statusBox.Child = new TextBlock { Text = "Package creation is in progress. Any partially created files and folders will remain on disk.", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = valFg };
            Grid.SetRow(statusLbl, 0); Grid.SetColumn(statusLbl, 0);
            Grid.SetRow(statusBox, 0); Grid.SetColumn(statusBox, 1);
            details.Children.Add(statusLbl); details.Children.Add(statusBox);

            // Row 1 — spacer
            details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            // Row 2 — Action box
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var noteLbl = new TextBlock { Text = "Action", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
            var noteBox = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6) };
            noteBox.Child = new TextBlock { Text = "Do you want to cancel it?", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = valFg };
            Grid.SetRow(noteLbl, 2); Grid.SetColumn(noteLbl, 0);
            Grid.SetRow(noteBox, 2); Grid.SetColumn(noteBox, 1);
            details.Children.Add(noteLbl); details.Children.Add(noteBox);

            // Row 3 — spacer
            details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            // Row 4 — Yes / No buttons equal width in col 1
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Style? sec = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;
            var btnGrid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2 };

            var btnYes = new Button { Content = "✔ Yes", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 4, 0) };
            ApApplyPrimaryLook(btnYes);
            btnYes.Click += (_, _) => { result = true; win.Close(); };

            var btnNo = new Button { Content = "✕ No", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), IsDefault = true };
            btnNo.Click += (_, _) => { result = false; win.Close(); };

            btnGrid.Children.Add(btnYes);
            btnGrid.Children.Add(btnNo);
            Grid.SetRow(btnGrid, 4); Grid.SetColumn(btnGrid, 1);
            details.Children.Add(btnGrid);
            var cancelRowIcon = ApDecisionIcon();
            Grid.SetRow(cancelRowIcon, 4); Grid.SetColumn(cancelRowIcon, 0);
            details.Children.Add(cancelRowIcon);

            root.Children.Add(details);
            win.ShowDialog();
            return result;
        }

        // ══════════════════════════════════════════════════════════════════
        //  COPY DIRECTORY (recursive)
        // ══════════════════════════════════════════════════════════════════
        private static void CopyDirectory(string src, string dest)
        {
            Directory.CreateDirectory(dest);

            // Normalize both paths so we can detect overlap (e.g. dest lives inside src)
            string srcFull  = IOPath.GetFullPath(src) .TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
            string destFull = IOPath.GetFullPath(dest).TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);

            foreach (string file in Directory.GetFiles(src))
            {
                string destFile = IOPath.Combine(dest, IOPath.GetFileName(file));
                try
                {
                    if (File.Exists(destFile))
                    {
                        var attrs = File.GetAttributes(destFile);
                        if ((attrs & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(destFile, attrs & ~FileAttributes.ReadOnly);
                    }
                    File.Copy(file, destFile, overwrite: true);
                }
                catch (UnauthorizedAccessException exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                catch (IOException exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }

            foreach (string dir in Directory.GetDirectories(src))
            {
                // Skip any subdirectory that IS or CONTAINS the destination folder —
                // this prevents an infinite recursive copy when the package output
                // folder happens to live inside the source folder (e.g. C:\Temp → C:\Temp\PackageName\…).
                string dirFull = IOPath.GetFullPath(dir).TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
                if (destFull.StartsWith(dirFull + IOPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(dirFull, destFull, StringComparison.OrdinalIgnoreCase))
                    continue;

                CopyDirectory(dir, IOPath.Combine(dest, IOPath.GetFileName(dir)));
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  CREATE MST via PowerShell  (wraps Windows Installer COM)
        // ══════════════════════════════════════════════════════════════════
        private static void CreateMstViaPs(string msiPath, string mstPath,
            List<AutoMstProperty> properties, bool removeDesktop, StringBuilder log)
        {
            // Pattern copied from CreateMSTPage - uses GetType().InvokeMember() for all COM calls.
            //
            // The whole COM body runs inside try/finally now: with $ErrorActionPreference =
            // 'Stop', ANY throw used to skip the Remove-Item at the end and strand the 29 MB
            // .msi_bak inside Package\Files - where the intunewin compressor then shipped it
            // in every package. The finally releases the COM objects (they hold the file
            // open), forces the RCW finalizers, and deletes the bak no matter how the body
            // ended; a C#-side retry below is the second line of defence.
            string bakPath = msiPath + "_bak";
            if (File.Exists(bakPath)) File.Delete(bakPath);
            File.Copy(msiPath, bakPath, overwrite: true);
            if (File.Exists(mstPath)) File.Delete(mstPath);

            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine("$msiOpenDatabaseModeReadOnly = 0");
            sb.AppendLine("$msiOpenDatabaseModeTransact = 1");
            sb.AppendLine("$msiTransformErrorNone = 0");
            sb.AppendLine("$msiTransformValidationNone = 0");
            sb.AppendLine("$windowsInstaller = $null; $database1 = $null; $database2 = $null");
            sb.AppendLine("try {");
            sb.AppendLine("$windowsInstaller = New-Object -ComObject windowsInstaller.Installer");
            sb.AppendLine("$database1 = $windowsInstaller.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $Null, $windowsInstaller, @('" + msiPath.Replace("'","''") + "', $msiOpenDatabaseModeReadOnly))");
            sb.AppendLine("$database2 = $windowsInstaller.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $Null, $windowsInstaller, @('" + bakPath.Replace("'","''") + "', $msiOpenDatabaseModeTransact))");

            // Apply property overrides — check existence first (matches CreateMSTPage pattern)
            sb.AppendLine("# Read existing properties into hashtable");
            sb.AppendLine("$existingProps = @{}");
            sb.AppendLine("$pqv = $database2.GetType().InvokeMember('OpenView', 'InvokeMethod', $Null, $database2, ('SELECT Property,Value FROM Property'))");
            sb.AppendLine("$pqv.GetType().InvokeMember('Execute', 'InvokeMethod', $Null, $pqv, $Null)");
            sb.AppendLine("while ($true) { $pr = $pqv.GetType().InvokeMember('Fetch', 'InvokeMethod', $Null, $pqv, $Null); if ($pr -eq $null) { break }; $pk = $pr.GetType().InvokeMember('StringData', 'GetProperty', $Null, $pr, 1); $existingProps[$pk] = $true }");
            sb.AppendLine("$pqv.GetType().InvokeMember('Close', 'InvokeMethod', $Null, $pqv, $Null)");
            sb.AppendLine("[System.Runtime.InteropServices.Marshal]::ReleaseComObject($pqv) | Out-Null");

            foreach (var p in properties ?? new List<AutoMstProperty>())
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Property)) continue;
                string rawValue = p.Value ?? "";
                string pn = p.Property.Replace("'", "''");
                string pv = rawValue.Replace("'", "''");

                // If value is empty — only update if property already exists; never INSERT empty
                // If value is non-empty — update if exists, insert if new
                string sqlU = "UPDATE `Property` SET `Value`='" + pv + "' WHERE `Property`='" + pn + "'";
                string sqlI = "INSERT INTO `Property` (`Property`, `Value`) VALUES ('" + pn + "', '" + pv + "')";

                sb.AppendLine("try {");
                if (string.IsNullOrEmpty(rawValue))
                {
                    // Only update — skip if property doesn't exist in this MSI
                    sb.AppendLine("  if ($existingProps['" + pn + "']) {");
                    sb.AppendLine("    $query = \"" + sqlU + "\"");
                    sb.AppendLine("    $view = $database2.GetType().InvokeMember('OpenView', 'InvokeMethod', $Null, $database2, ($query))");
                    sb.AppendLine("    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $Null, $view, $Null)");
                    sb.AppendLine("    $view.GetType().InvokeMember('Close', 'InvokeMethod', $Null, $view, $Null)");
                    sb.AppendLine("    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($view) | Out-Null");
                    sb.AppendLine("    Write-Output 'SET: " + p.Property + " (update only)'");
                    sb.AppendLine("  } else { Write-Output 'SKIP: " + p.Property + " (not in MSI, empty value)' }");
                }
                else
                {
                    sb.AppendLine("  if ($existingProps['" + pn + "']) { $query = \"" + sqlU + "\" } else { $query = \"" + sqlI + "\" }");
                    sb.AppendLine("  $view = $database2.GetType().InvokeMember('OpenView', 'InvokeMethod', $Null, $database2, ($query))");
                    sb.AppendLine("  $view.GetType().InvokeMember('Execute', 'InvokeMethod', $Null, $view, $Null)");
                    sb.AppendLine("  $view.GetType().InvokeMember('Close', 'InvokeMethod', $Null, $view, $Null)");
                    sb.AppendLine("  [System.Runtime.InteropServices.Marshal]::ReleaseComObject($view) | Out-Null");
                    sb.AppendLine("  Write-Output 'SET: " + p.Property + " = " + rawValue.Replace("'", "''") + "'");
                }
                sb.AppendLine("} catch { Write-Output ('ERR: " + p.Property + " - ' + $_.Exception.Message) }");
            }

            // Remove Desktop Shortcut (same logic as CreateMSTPage)
            if (removeDesktop)
            {
                sb.AppendLine("# Remove desktop shortcuts");
                sb.AppendLine("try {");
                sb.AppendLine("  $dsQuery = 'SELECT Shortcut, Name, Directory_ FROM Shortcut'");
                sb.AppendLine("  $dsView = $database2.GetType().InvokeMember('OpenView', 'InvokeMethod', $Null, $database2, ($dsQuery))");
                sb.AppendLine("  $dsView.GetType().InvokeMember('Execute', 'InvokeMethod', $Null, $dsView, $Null)");
                sb.AppendLine("  $dsToDelete = @{}");
                sb.AppendLine("  while ($true) {");
                sb.AppendLine("    $dsRec = $dsView.GetType().InvokeMember('Fetch', 'InvokeMethod', $Null, $dsView, $Null)");
                sb.AppendLine("    if ($dsRec -eq $null) { break }");
                sb.AppendLine("    $dsKey  = $dsRec.GetType().InvokeMember('StringData', 'GetProperty', $Null, $dsRec, 1)");
                sb.AppendLine("    $dsDir  = $dsRec.GetType().InvokeMember('StringData', 'GetProperty', $Null, $dsRec, 3)");
                sb.AppendLine("    if ($dsDir -eq 'DesktopFolder') { $dsToDelete[$dsKey] = $dsKey }");
                sb.AppendLine("  }");
                sb.AppendLine("  $dsView.GetType().InvokeMember('Close', 'InvokeMethod', $Null, $dsView, $Null)");
                sb.AppendLine("  [System.Runtime.InteropServices.Marshal]::ReleaseComObject($dsView) | Out-Null");
                sb.AppendLine("  foreach ($dsKey in $dsToDelete.Keys) {");
                sb.AppendLine("    $delQuery = \"DELETE FROM ``Shortcut`` WHERE ``Shortcut``='$dsKey'\"");
                sb.AppendLine("    $delView = $database2.GetType().InvokeMember('OpenView', 'InvokeMethod', $Null, $database2, ($delQuery))");
                sb.AppendLine("    $delView.GetType().InvokeMember('Execute', 'InvokeMethod', $Null, $delView, $Null)");
                sb.AppendLine("    $delView.GetType().InvokeMember('Close', 'InvokeMethod', $Null, $delView, $Null)");
                sb.AppendLine("    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($delView) | Out-Null");
                sb.AppendLine("    Write-Output ('Removed desktop shortcut: ' + $dsKey)");
                sb.AppendLine("  }");
                sb.AppendLine("} catch { Write-Output ('Desktop shortcut removal skipped: ' + $_.Exception.Message) }");
            }

            sb.AppendLine("$database2.GetType().InvokeMember('Commit', 'InvokeMethod', $Null, $database2, $Null)");
            // GenerateTransform returns FALSE when the two databases are identical - and then
            // writes NOTHING. Calling CreateTransformSummaryInfo against the missing file was
            // the COMException this whole flow died on. Only stamp the summary when a
            // transform actually exists; otherwise say exactly why there is no file.
            sb.AppendLine("$gt = $database2.GetType().InvokeMember('GenerateTransform', 'InvokeMethod', $Null, $database2, @($database1, '" + mstPath.Replace("'","''") + "'))");
            sb.AppendLine("if ($gt) {");
            sb.AppendLine("  $database2.GetType().InvokeMember('CreateTransformSummaryInfo', 'InvokeMethod', $Null, $database2, @($database1, '" + mstPath.Replace("'","''") + "', $msiTransformErrorNone, $msiTransformValidationNone))");
            sb.AppendLine("  Write-Output 'MST_OK'");
            sb.AppendLine("} else {");
            sb.AppendLine("  Write-Output 'MST_NODIFF: the configured changes made no difference to this MSI (values already identical) - no transform written'");
            sb.AppendLine("}");
            sb.AppendLine("} finally {");
            sb.AppendLine("if ($database1 -ne $null) { [System.Runtime.InteropServices.Marshal]::ReleaseComObject($database1) | Out-Null }");
            sb.AppendLine("if ($database2 -ne $null) { [System.Runtime.InteropServices.Marshal]::ReleaseComObject($database2) | Out-Null }");
            sb.AppendLine("if ($windowsInstaller -ne $null) { [System.Runtime.InteropServices.Marshal]::ReleaseComObject($windowsInstaller) | Out-Null }");
            sb.AppendLine("[System.GC]::Collect(); [System.GC]::WaitForPendingFinalizers()");
            sb.AppendLine("Remove-Item -LiteralPath '" + bakPath.Replace("'","''") + "' -Force -ErrorAction SilentlyContinue");
            sb.AppendLine("}");

            string script = sb.ToString();
            RunPsScript(script, log, isScript: true);

            // Second line of defence: if the script died before its finally could run (host
            // killed, PS missing), the bak must still never ship inside the package. The
            // Installer COM can hold the handle for a beat after process exit, hence the
            // short retry.
            for (int attempt = 0; attempt < 3 && File.Exists(bakPath); attempt++)
            {
                try { File.Delete(bakPath); }
                catch { System.Threading.Thread.Sleep(300); }
            }
            if (File.Exists(bakPath))
                log.AppendLine("  ⚠ Could not delete " + IOPath.GetFileName(bakPath) + " - remove it manually or it will be packaged.");
        }



        // ══════════════════════════════════════════════════════════════════
        //  PATCH Invoke-AppDeployToolkit.ps1
        // ══════════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════════
        //  PATCH Invoke-AppDeployToolkit.ps1  (MSI source flow)
        //
        //  Scans Files\ (all depths) for every .msi and .exe and injects
        //  one command per file into the Install / Uninstall sections:
        //
        //  .msi  → comment from MSI COM (Manufacturer ProductName ProductVersion)
        //          or from registry if app is installed
        //          Start-ADTMsiProcess -Action 'Install' -FilePath "...\rel\path.msi"
        //
        //  .exe  → comment from registry (Publisher DisplayName DisplayVersion)
        //          or from FileVersionInfo if not installed (#Publisher Name Version)
        //          or just #filename.exe as last resort
        //          Start-ADTProcess -FilePath "...\rel\path.exe"
        // ══════════════════════════════════════════════════════════════════
        private static void PatchInvokeScript(
            string scriptPath, string msiName, string mstName,
            string productCode, string closeApps, StringBuilder log,
            string appName = "", string appVendor = "", string appVersion = "",
            string appRevision = "", string msiArgumentList = "", string additionalArgs = "",
            bool addMsi = true, bool addMst = true, bool addMsp = true,
            bool addExe = true, bool addBat = true, bool addIss = true,
            bool addReg = true, bool addCer = true, bool addInf = true,
            AutoPackageConfig? cfg = null)
        {
            if (!File.Exists(scriptPath)) return;
            string content   = File.ReadAllText(scriptPath);
            string scriptDir = IOPath.GetDirectoryName(scriptPath)!;
            string filesDir  = IOPath.Combine(scriptDir, "Files");

            // ── 1. Session parameters ─────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(appName))
                content = PatchSessionParam(content, "AppName",   $"'{appName}'",   log);
            if (!string.IsNullOrWhiteSpace(appVendor))
                content = PatchSessionParam(content, "AppVendor", $"'{appVendor}'", log);
            if (!string.IsNullOrWhiteSpace(appVersion))
                content = PatchSessionParam(content, "AppVersion",$"'{appVersion}'",log);
            content = PatchSessionParam(content, "AppRevision",
                $"'{(string.IsNullOrWhiteSpace(appRevision) ? "01" : appRevision)}'", log);
            content = PatchSessionParam(content, "AppScriptDate",
                $"'{DateTime.Now:yyyy-MM-dd}'", log);

            // ── InstallName / InstallTitle = package name (Vendor_App_Version_Revision) ──
            if (cfg?.AddInstallName == true || cfg?.AddInstallTitle == true)
            {
                // Build package name the same way as TxtPackageName: Vendor_AppName_Version_Revision
                string pkgName = string.Join("_",
                    new[] { appVendor, appName, appVersion, appRevision }
                    .Select(s => s?.Trim().Replace(" ", "") ?? "")
                    .Where(s => !string.IsNullOrEmpty(s)));
                if (!string.IsNullOrWhiteSpace(pkgName))
                {
                    if (cfg?.AddInstallName  == true)
                        content = PatchSessionParam(content, "InstallName",  $"'{pkgName}'", log);
                    if (cfg?.AddInstallTitle == true)
                        content = PatchSessionParam(content, "InstallTitle", $"'{pkgName}'", log);
                }
            }

            // Auto-collect AppProcessesToClose from all files in Files\
            // Merges: template existing values + UI closeApps + auto-detected from EXE/MSI
            {
                // Step 1: Read existing values already in the ps1 (e.g. from PackageTemplateFolder)
                var existingProcs = new System.Collections.Generic.List<string>();
                var existingMatch = System.Text.RegularExpressions.Regex.Match(
                    content,
                    @"AppProcessesToClose\s*=\s*@\(([^)]*)\)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (existingMatch.Success)
                {
                    // Parse 'proc1', 'proc2' etc. from the existing array
                    foreach (System.Text.RegularExpressions.Match m in
                        System.Text.RegularExpressions.Regex.Matches(
                            existingMatch.Groups[1].Value, @"'([^']+)'"))
                    {
                        string proc = m.Groups[1].Value.Trim();
                        if (!string.IsNullOrWhiteSpace(proc))
                            existingProcs.Add(proc);
                    }
                }

                // Step 2: Start with existing values, then add UI closeApps
                var autoProcs = new System.Collections.Generic.List<string>(existingProcs);
                if (!string.IsNullOrWhiteSpace(closeApps))
                {
                    foreach (var p in closeApps.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
                        if (!autoProcs.Contains(p, StringComparer.OrdinalIgnoreCase))
                            autoProcs.Add(p);
                }

                if (Directory.Exists(filesDir))
                {
                    log.AppendLine($"  [CloseApps] Scanning filesDir={filesDir}");
                    var allExeFiles = Directory.GetFiles(filesDir, "*.exe", SearchOption.AllDirectories);
                    var allMsiFiles = Directory.GetFiles(filesDir, "*.msi", SearchOption.AllDirectories);
                    log.AppendLine($"  [CloseApps] Found {allExeFiles.Length} EXEs, {allMsiFiles.Length} MSIs");

                    // EXEs — use FileVersionInfo ProductName as hint
                    foreach (string ef in Directory.GetFiles(filesDir, "*.exe", SearchOption.AllDirectories))
                    {
                        string hint = "";
                        try { var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(ef); hint = vi.ProductName?.Trim() ?? ""; } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        if (string.IsNullOrWhiteSpace(hint)) hint = IOPath.GetFileNameWithoutExtension(ef);
                        var regE = LookupInstalledByExe(ef, hint);
                        string procName = DeriveCloseApps(regE);
                        if (string.IsNullOrWhiteSpace(procName)) procName = IOPath.GetFileNameWithoutExtension(ef);
                        log.AppendLine($"  [CloseApps] EXE={IOPath.GetFileName(ef)} hint={hint} reg={regE?.DisplayName ?? "null"} proc={procName}");
                        if (!string.IsNullOrWhiteSpace(procName) &&
                            !autoProcs.Contains(procName, StringComparer.OrdinalIgnoreCase))
                            autoProcs.Add(procName);
                    }
                    // MSIs — STA thread for COM
                    foreach (string mf in Directory.GetFiles(filesDir, "*.msi", SearchOption.AllDirectories))
                    {
                        var msiPropsA = ReadMsiPropertiesSTA(mf);
                        msiPropsA.TryGetValue("ProductCode", out var msiPcA);
                        msiPropsA.TryGetValue("ProductName", out var msiNameA);
                        ExeInstalledInfo? regM = !string.IsNullOrWhiteSpace(msiNameA)
                            ? LookupInstalledByExe("", msiNameA) : null;
                        if (regM == null && !string.IsNullOrWhiteSpace(msiPcA))
                            regM = LookupInstalledByProductCode(msiPcA);
                        string msiProc = DeriveCloseApps(regM);
                        log.AppendLine($"  [CloseApps] MSI={IOPath.GetFileName(mf)} name={msiNameA} pc={msiPcA} reg={regM?.DisplayName ?? "null"} proc={msiProc}");
                        if (!string.IsNullOrWhiteSpace(msiProc) &&
                            !autoProcs.Contains(msiProc, StringComparer.OrdinalIgnoreCase))
                            autoProcs.Add(msiProc);
                    }
                }

                if (autoProcs.Count > 0)
                {
                    content = PatchSessionParam(content, "AppProcessesToClose",
                        "@(" + string.Join(", ", autoProcs.Select(p => $"'{p}'")) + ")", log);
                    log.AppendLine($"  CloseApps: {string.Join(", ", autoProcs)}");
                }
            }

            // ── 2. Collect all installer files under Files\ ──────────────
            if (!Directory.Exists(filesDir))
            {
                File.WriteAllText(scriptPath, content, new UTF8Encoding(true));
                return;
            }

            string RelPath(string full) => full.Substring(filesDir.Length)
                .TrimStart(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);

            // ── Helper: MsiComment ────────────────────────────────────────
            string MsiComment(string msiPath, string pc)
            {
                ExeInstalledInfo? reg = !string.IsNullOrWhiteSpace(pc)
                    ? LookupInstalledByProductCode(pc) : null;
                // Fallback: ProductName hint when product code key not found
                if (reg == null)
                {
                    var propsM = ReadMsiPropertiesSTA(msiPath);
                    propsM.TryGetValue("ProductName", out var pnM);
                    if (!string.IsNullOrWhiteSpace(pnM)) reg = LookupInstalledByExe("", pnM);
                }
                if (reg != null)
                {
                    var rp = new[] { reg.Publisher, reg.DisplayName, reg.DisplayVersion }
                        .Where(p => !string.IsNullOrWhiteSpace(p));
                    if (rp.Any()) return "    # " + string.Join(" ", rp);
                }
                var props = ReadMsiPropertiesSTA(msiPath);
                props.TryGetValue("Manufacturer",   out var mfr);
                props.TryGetValue("ProductName",    out var pn);
                props.TryGetValue("ProductVersion", out var pv);
                var fp = new[] { mfr, pn, pv }.Where(p => !string.IsNullOrWhiteSpace(p));
                return fp.Any() ? "    # " + string.Join(" ", fp) : "    # " + IOPath.GetFileName(msiPath);
            }

            // ── Helper: ExeInfo (comment + registry uninstall) ────────────
            (string comment, string regUninstall) ExeInfo(string exePath)
            {
                string viVendor = "", viName = "", viVer = "";
                if (File.Exists(exePath))
                {
                    try
                    {
                        var vi = FileVersionInfo.GetVersionInfo(exePath);
                        viVendor = vi.CompanyName?.Trim()  ?? "";
                        viName   = vi.ProductName?.Trim()  ?? "";
                        viVer    = (vi.ProductVersion ?? vi.FileVersion)?.Trim() ?? "";
                    }
                    catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
                string nameHint = !string.IsNullOrWhiteSpace(viName) ? viName
                    : IOPath.GetFileNameWithoutExtension(exePath);
                ExeInstalledInfo? reg = LookupInstalledByExe(exePath, nameHint);
                if (reg != null && !string.IsNullOrWhiteSpace(reg.DisplayName))
                {
                    var rp = new[] { reg.Publisher, reg.DisplayName, reg.DisplayVersion }
                        .Where(p => !string.IsNullOrWhiteSpace(p));
                    string uninstStr = !string.IsNullOrWhiteSpace(reg.QuietUninstallString)
                        ? reg.QuietUninstallString : reg.UninstallString;
                    return ("    # " + string.Join(" ", rp), uninstStr);
                }
                var fp = new[] { viVendor, viName, viVer }.Where(p => !string.IsNullOrWhiteSpace(p));
                string c = fp.Any() ? "    # " + string.Join(" ", fp) : "    # " + IOPath.GetFileName(exePath);
                return (c, "");
            }

            // ── Helper: parse UninstallString → (path, args) ─────────────
            static (string path, string args) ParseUninstallString(string uninstStr)
            {
                string uPath = uninstStr.Trim(), uArgs = "";
                if (uPath.StartsWith("\""))
                {
                    int end = uPath.IndexOf('"', 1);
                    if (end > 1) { uArgs = uPath[(end + 1)..].Trim(); uPath = uPath[1..end]; }
                }
                else
                {
                    int exeEnd = uPath.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                    if (exeEnd >= 0) { uArgs = uPath[(exeEnd + 4)..].Trim(); uPath = uPath[..(exeEnd + 4)]; }
                }
                return (uPath, uArgs);
            }

            var installSb   = new System.Text.StringBuilder();
            var uninstallSb = new System.Text.StringBuilder();

            // Sort: root files before subdirectory files
            string[] Collect(string pattern) =>
                Directory.Exists(filesDir)
                    ? Directory.GetFiles(filesDir, pattern, SearchOption.AllDirectories)
                              .OrderBy(f => IOPath.GetDirectoryName(f)!.Length).ThenBy(f => f).ToArray()
                    : Array.Empty<string>();

            var allMsi = Collect("*.msi");
            var allMsp = Collect("*.msp");
            var allExe = Collect("*.exe");
            var allBat = Collect("*.bat");
            var allIss = Collect("*.iss");
            var allReg = Collect("*.reg");
            var allCer = Collect("*.cer");
            var allInf = Collect("*.inf");

            // ── MSI commands ──────────────────────────────────────────────
            if (addMsi)
            {
                foreach (string msiPath in allMsi)
                {
                    string msiRel  = RelPath(msiPath);
                    string msiDir2 = IOPath.GetDirectoryName(msiPath)!;
                    string baseName = IOPath.GetFileNameWithoutExtension(msiPath);

                    // Read product code from this MSI directly
                    var msiProps2 = ReadMsiPropertiesSTA(msiPath);
                    msiProps2.TryGetValue("ProductCode", out var pc2Raw);
                    string pc2 = pc2Raw ?? "";
                    if (string.IsNullOrWhiteSpace(pc2) && !string.IsNullOrWhiteSpace(productCode)
                        && string.Equals(IOPath.GetFileName(msiPath), msiName, StringComparison.OrdinalIgnoreCase))
                        pc2 = productCode;

                    string comment = MsiComment(msiPath, pc2);

                    // Resolve MST: same base name only (setup.msi ↔ setup.mst)
                    string mstRel = "";
                    if (addMst)
                    {
                        string mstSameName = baseName + ".mst";
                        string mstNear = IOPath.Combine(msiDir2, mstSameName);
                        string mstRoot = IOPath.Combine(filesDir, mstSameName);
                        if      (File.Exists(mstNear)) mstRel = RelPath(mstNear);
                        else if (File.Exists(mstRoot)) mstRel = mstSameName;
                        // Also honour explicit mstName passed in (from UI Create MST)
                        if (string.IsNullOrEmpty(mstRel) && !string.IsNullOrEmpty(mstName))
                        {
                            string mstExplicitNear = IOPath.Combine(msiDir2, mstName);
                            string mstExplicitRoot = IOPath.Combine(filesDir, mstName);
                            if      (File.Exists(mstExplicitNear)) mstRel = RelPath(mstExplicitNear);
                            else if (File.Exists(mstExplicitRoot)) mstRel = mstName;
                        }
                    }

                    // Install
                    if (!string.IsNullOrEmpty(comment)) installSb.AppendLine(comment.Replace("    # ", "    # Install "));
                    installSb.Append($"    Start-ADTMsiProcess -Action 'Install' -FilePath \"$($adtSession.DirFiles)\\{msiRel}\"");
                    if (!string.IsNullOrEmpty(mstRel))                installSb.Append($" -Transforms \"$($adtSession.DirFiles)\\{mstRel}\"");
                    if (!string.IsNullOrWhiteSpace(msiArgumentList)) installSb.Append($" -ArgumentList ('{msiArgumentList}')");
                    if (!string.IsNullOrWhiteSpace(additionalArgs))  installSb.Append($" -AdditionalArgumentList ('{additionalArgs}')");
                    installSb.AppendLine();
                    installSb.AppendLine();

                    // Uninstall
                    if (!string.IsNullOrWhiteSpace(pc2))
                    {
                        if (!string.IsNullOrEmpty(comment)) uninstallSb.AppendLine(comment.Replace("    # ", "    # Uninstall "));
                        // Double quotes around the ProductCode, single quotes around Action. The
                        // GUID is a value the packager substitutes, so it goes in an expandable
                        // string like every other substituted value in this block; Action is a
                        // fixed literal and stays in a verbatim one.
                        uninstallSb.AppendLine($"    Start-ADTMsiProcess -Action 'Uninstall' -ProductCode \"{pc2}\"");
                        uninstallSb.AppendLine();
                    }
                    log.AppendLine($"  MSI Install: {msiRel}" + (string.IsNullOrEmpty(mstRel) ? "" : $" +MST:{mstRel}"));
                }
            }

            // ── MSP commands ──────────────────────────────────────────────
            if (addMsp)
            {
                foreach (string mspPath in allMsp)
                {
                    string mspRel = RelPath(mspPath);
                    string mspName = IOPath.GetFileName(mspPath);
                    installSb.AppendLine($"    # Install {mspName}");
                    installSb.AppendLine($"    Start-ADTMsiProcess -Action 'Patch' -FilePath \"$($adtSession.DirFiles)\\{mspRel}\"");
                    installSb.AppendLine();
                    log.AppendLine($"  MSP Install: {mspRel}");
                }
            }

            // ── EXE commands ──────────────────────────────────────────────
            if (addExe)
            {
                // Skip EXEs that are paired with an ISS file (handled in ISS section)
                var issBaseNames = new HashSet<string>(
                    allIss.Select(f => IOPath.GetFileNameWithoutExtension(f).ToLowerInvariant()),
                    StringComparer.OrdinalIgnoreCase);

                foreach (string exePath in allExe)
                {
                    string exeBase = IOPath.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
                    if (addIss && issBaseNames.Contains(exeBase)) continue; // handled by ISS block

                    string exeRel = RelPath(exePath);
                    var (comment, regUninstall) = ExeInfo(exePath);

                    // Silent install switch. There was a table of product names here, matched on
                    // the exe filename; it is gone, along with every other compiled list of other
                    // people's software. /S is the NSIS default and by far the most common, and an
                    // explicit switch typed into the UI still wins over it everywhere it is offered.
                    string instSwitch = "/S";
                    string instArgsPart = $" -ArgumentList '{instSwitch}'";

                    if (!string.IsNullOrEmpty(comment)) installSb.AppendLine(comment.Replace("    # ", "    # Install "));
                    installSb.AppendLine($"    Start-ADTProcess -FilePath \"$($adtSession.DirFiles)\\{exeRel}\"{instArgsPart} -WindowStyle \"Hidden\"");
                    installSb.AppendLine();
                    log.AppendLine($"  EXE Install: {exeRel}");

                    // Uninstall from registry
                    if (!string.IsNullOrWhiteSpace(regUninstall) &&
                        !regUninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
                    {
                        var (uPath, uArgs) = ParseUninstallString(regUninstall);
                        // If registry has no args, fall back to DB uninstall switch → /S
                        if (string.IsNullOrWhiteSpace(uArgs))
                            uArgs = "/S";
                        string psPath    = ConvertToEnvSystemDrivePath(uPath);
                        string uArgsPart = $" -ArgumentList '{uArgs}'";
                        if (!string.IsNullOrEmpty(comment)) uninstallSb.AppendLine(comment.Replace("    # ", "    # Uninstall "));
                        AppendGuardedExeUninstall(uninstallSb, psPath, uArgsPart);
                        uninstallSb.AppendLine();
                        log.AppendLine($"  EXE Uninstall (registry): {psPath}");
                    }
                }
            }

            // ── ISS commands (InstallShield) ──────────────────────────────
            if (addIss)
            {
                foreach (string issPath in allIss)
                {
                    string issDir  = IOPath.GetDirectoryName(issPath)!;
                    string issBase = IOPath.GetFileNameWithoutExtension(issPath);
                    // Paired EXE must have the same base name
                    string pairedExe = IOPath.Combine(issDir, issBase + ".exe");
                    if (!File.Exists(pairedExe)) continue; // no matching EXE - skip

                    string issRel = RelPath(issPath);
                    string exeRel = RelPath(pairedExe);
                    var (comment, _) = ExeInfo(pairedExe);

                    if (!string.IsNullOrEmpty(comment)) installSb.AppendLine(comment);
                    installSb.AppendLine($"    Start-ADTProcess -FilePath \"$($adtSession.DirFiles)\\{exeRel}\" -ArgumentList \"-s -f1`\"$($adtSession.DirFiles)\\{issRel}`\"\" -WindowStyle \"Hidden\"");
                    installSb.AppendLine();
                    log.AppendLine($"  ISS Install: {exeRel} + {issRel}");
                }
            }

            // ── BAT commands ──────────────────────────────────────────────
            if (addBat)
            {
                foreach (string batPath in allBat)
                {
                    string batRel  = RelPath(batPath);
                    string batName = IOPath.GetFileName(batPath);
                    installSb.AppendLine($"    # Run {batName}");
                    installSb.AppendLine($"    Start-ADTProcess -FilePath \"$($adtSession.DirFiles)\\{batRel}\" -WindowStyle \"Hidden\"");
                    installSb.AppendLine();
                    log.AppendLine($"  BAT Install: {batRel}");
                }
            }

            // ── REG commands ──────────────────────────────────────────────
            if (addReg)
            {
                foreach (string regPath in allReg)
                {
                    string regRel  = RelPath(regPath);
                    string regName = IOPath.GetFileName(regPath);
                    installSb.AppendLine($"    # Run {regName}");
                    installSb.AppendLine($"    Start-ADTProcess -FilePath \"regedit.exe\" -ArgumentList \"/s `\"$($adtSession.DirFiles)\\{regRel}`\"\" -WindowStyle \"Hidden\"");
                    installSb.AppendLine();
                    log.AppendLine($"  REG Install: {regRel}");
                }
            }

            // ── CER commands ──────────────────────────────────────────────
            if (addCer)
            {
                foreach (string cerPath in allCer)
                {
                    string cerRel  = RelPath(cerPath);
                    string cerName = IOPath.GetFileName(cerPath);

                    // Try to get cert info from the machine certificate store
                    string certComment = $"    # Install {cerName}";
                    string certSerial  = "";
                    try
                    {
                        var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(cerPath);
                        string cn = cert.GetNameInfo(
                            System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
                        certSerial = cert.SerialNumber ?? "";
                        if (!string.IsNullOrWhiteSpace(cn))
                            certComment = $"    # Install {cn}";
                        // Check if already in TrustedPublisher store
                        using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                            "TrustedPublisher",
                            System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
                        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                        var existing = store.Certificates.Find(
                            System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint,
                            cert.Thumbprint, false);
                        if (existing.Count > 0 && !string.IsNullOrWhiteSpace(cn))
                            certComment = $"    # Install {cn}";
                    }
                    catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                    // Install
                    installSb.AppendLine(certComment);
                    installSb.AppendLine($"    Start-ADTProcess -FilePath \"certutil.exe\" -ArgumentList \"-addstore TrustedPublisher `\"$($adtSession.DirFiles)\\{cerRel}`\"\" -WindowStyle \"Hidden\"");
                    installSb.AppendLine();
                    log.AppendLine($"  CER Install: {cerRel}");

                    // Uninstall — only if cert has a serial number
                    if (!string.IsNullOrWhiteSpace(certSerial))
                    {
                        string uninstCertComment = certComment.Replace("# Install", "# Uninstall");
                        uninstallSb.AppendLine(uninstCertComment);
                        uninstallSb.AppendLine($"    Start-ADTProcess -FilePath \"certutil.exe\" -ArgumentList \"-delstore TrustedPublisher {certSerial}\" -WindowStyle \"Hidden\"");
                        uninstallSb.AppendLine();
                        log.AppendLine($"  CER Uninstall: serial={certSerial}");
                    }
                    else
                    {
                        log.AppendLine($"  CER: no serial number found - uninstall skipped");
                    }
                }
            }

            // ── INF commands ──────────────────────────────────────────────
            if (addInf)
            {
                foreach (string infPath in allInf)
                {
                    string infRel  = RelPath(infPath);
                    string infName = IOPath.GetFileName(infPath);

                    // Install
                    installSb.AppendLine($"    # Install {infName}");
                    installSb.AppendLine($"    Start-ADTProcess -FilePath \"pnputil.exe\" -ArgumentList \"/add-driver `\"$($adtSession.DirFiles)\\{infRel}`\" /install /force\" -WindowStyle \"Hidden\" -IgnoreExitCodes '512'");
                    installSb.AppendLine();

                    // Uninstall
                    uninstallSb.AppendLine($"    # Uninstall {infName}");
                    uninstallSb.AppendLine($"    Start-ADTProcess -FilePath \"pnputil.exe\" -ArgumentList \"/delete-driver `\"$($adtSession.DirFiles)\\{infRel}`\" /uninstall /force\" -WindowStyle \"Hidden\"");
                    uninstallSb.AppendLine();
                    log.AppendLine($"  INF Install/Uninstall: {infRel}");
                }
            }

            // ── 3. Inject ─────────────────────────────────────────────────
            string installBlock   = installSb.ToString().TrimEnd();
            string uninstallBlock = uninstallSb.ToString().TrimEnd();

            if (!string.IsNullOrEmpty(installBlock))
                content = InjectAfterComment(content,
                    "## <Perform Installation tasks here>", installBlock, log);
            if (!string.IsNullOrEmpty(uninstallBlock))
                content = InjectAfterComment(content,
                    "## <Perform Uninstallation tasks here>", uninstallBlock, log);

            // ── ARP Flags (NoModify / NoRepair / NoRemove) ────────────────
            // Inject Set-ADTRegistryKey lines after ## <Perform Post-Installation tasks here>
            // only when the JSON config explicitly sets each flag to "Yes" / "true".
            bool doNoModify = cfg?.AddNoModify ?? false;
            bool doNoRepair = cfg?.AddNoRepair ?? false;
            bool doNoRemove = cfg?.AddNoRemove ?? false;

            if (doNoModify || doNoRepair || doNoRemove)
            {
                const string POST_MARKER = "## <Perform Post-Installation tasks here>";

                // Fallback label from caller-supplied app identity
                string arpFallbackLabel = string.Join(" ", new[] { appVendor, appName, appVersion }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

                // Collect all uninstall registry key paths + per-app display labels.
                // Dictionary: full HKEY_... path → "Publisher DisplayName DisplayVersion" label
                var uninstallRegKeyMap = new System.Collections.Generic.Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

                // Helper: normalise HKLM:\... or HKCU:\... to full HKEY_... form
                static string Normalise(string path) => path
                    .Replace("HKLM:\\", "HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
                    .Replace("HKCU:\\", "HKEY_CURRENT_USER\\",  StringComparison.OrdinalIgnoreCase)
                    .Replace("HKLM\\",  "HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
                    .Replace("HKCU\\",  "HKEY_CURRENT_USER\\",  StringComparison.OrdinalIgnoreCase);

                // Scan all EXEs in Files\ to collect registry entries
                if (Directory.Exists(filesDir))
                {
                    foreach (string ef in Directory.GetFiles(filesDir, "*.exe", SearchOption.AllDirectories))
                    {
                        string hint = "";
                        try { var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(ef); hint = vi.ProductName?.Trim() ?? ""; } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        if (string.IsNullOrWhiteSpace(hint)) hint = IOPath.GetFileNameWithoutExtension(ef);
                        var regE = LookupInstalledByExe(ef, hint);
                        if (regE != null && !string.IsNullOrWhiteSpace(regE.RegKeyPath))
                        {
                            string litPath = Normalise(regE.RegKeyPath);
                            string label   = string.Join(" ", new[] { regE.Publisher, regE.DisplayName, regE.DisplayVersion }
                                .Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!uninstallRegKeyMap.ContainsKey(litPath))
                                uninstallRegKeyMap[litPath] = label;
                        }
                    }
                    // Scan MSIs in Files\
                    foreach (string mf in Directory.GetFiles(filesDir, "*.msi", SearchOption.AllDirectories))
                    {
                        var msiPropsA = ReadMsiPropertiesSTA(mf);
                        msiPropsA.TryGetValue("ProductCode", out var msiPcA);
                        msiPropsA.TryGetValue("ProductName", out var msiPnA);
                        ExeInstalledInfo? regM = !string.IsNullOrWhiteSpace(msiPcA)
                            ? LookupInstalledByProductCode(msiPcA) : null;
                        if (regM == null && !string.IsNullOrWhiteSpace(msiPnA))
                            regM = LookupInstalledByExe("", msiPnA);
                        if (regM != null && !string.IsNullOrWhiteSpace(regM.RegKeyPath))
                        {
                            string litPath = Normalise(regM.RegKeyPath);
                            string label   = string.Join(" ", new[] { regM.Publisher, regM.DisplayName, regM.DisplayVersion }
                                .Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!uninstallRegKeyMap.ContainsKey(litPath))
                                uninstallRegKeyMap[litPath] = label;
                        }
                    }
                }
                // If productCode was passed directly, add its registry entry too
                if (!string.IsNullOrWhiteSpace(productCode))
                {
                    var regPc = LookupInstalledByProductCode(productCode);
                    if (regPc != null && !string.IsNullOrWhiteSpace(regPc.RegKeyPath))
                    {
                        string litPath = Normalise(regPc.RegKeyPath);
                        string label   = string.Join(" ", new[] { regPc.Publisher, regPc.DisplayName, regPc.DisplayVersion }
                            .Where(s => !string.IsNullOrWhiteSpace(s)));
                        if (!uninstallRegKeyMap.ContainsKey(litPath))
                            uninstallRegKeyMap[litPath] = label;
                    }
                }

                // Fallback: scan all three uninstall hives directly by appName when filesDir/productCode scan finds nothing
                if (uninstallRegKeyMap.Count == 0 && !string.IsNullOrWhiteSpace(appName))
                {
                    log.AppendLine($"  ARP: filesDir/productCode scan found nothing - scanning registry hives by AppName '{appName}'…");
                    var hiveRoots = new[]
                    {
                        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",           Microsoft.Win32.Registry.LocalMachine),
                        (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", Microsoft.Win32.Registry.LocalMachine),
                        (@"Software\Microsoft\Windows\CurrentVersion\Uninstall",           Microsoft.Win32.Registry.CurrentUser),
                    };
                    foreach (var (subPath, hive) in hiveRoots)
                    {
                        try
                        {
                            using var parent = hive.OpenSubKey(subPath);
                            if (parent == null) continue;
                            foreach (string sub in parent.GetSubKeyNames())
                            {
                                try
                                {
                                    using var k = parent.OpenSubKey(sub);
                                    if (k == null) continue;
                                    string dn = k.GetValue("DisplayName") as string ?? "";
                                    if (string.IsNullOrWhiteSpace(dn)) continue;
                                    if (!dn.Contains(appName, StringComparison.OrdinalIgnoreCase) &&
                                        !appName.Contains(dn,  StringComparison.OrdinalIgnoreCase)) continue;
                                    string pub     = k.GetValue("Publisher")      as string ?? "";
                                    string ver     = k.GetValue("DisplayVersion") as string ?? "";
                                    string hiveName = hive == Microsoft.Win32.Registry.LocalMachine
                                        ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
                                    string litPath = $"{hiveName}\\{subPath}\\{sub}";
                                    string label   = string.Join(" ", new[] { pub, dn, ver }
                                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                                    if (!uninstallRegKeyMap.ContainsKey(litPath))
                                    {
                                        uninstallRegKeyMap[litPath] = label;
                                        log.AppendLine($"  ARP: found via hive scan → {litPath}");
                                    }
                                }
                                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                            }
                        }
                        catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    }
                    if (uninstallRegKeyMap.Count == 0)
                        log.AppendLine($"  ARP: app '{appName}' not found in any uninstall hive on this machine - skipped.");
                }

                if (uninstallRegKeyMap.Count > 0)
                {
                    var arpLines = new System.Collections.Generic.List<string>();

                    foreach (var kvp in uninstallRegKeyMap)
                    {
                        string litPath = kvp.Key;

                        // Try to open the live registry key to check existing values.
                        // If the app is not yet installed on THIS machine, liveKey == null —
                        // we still inject so the commands run on the TARGET machine after install.
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
                        catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                        var appArpLines = new System.Collections.Generic.List<string>();

                        if (doNoModify)
                        {
                            bool alreadySet = liveKey?.GetValue("NoModify") != null;
                            if (!alreadySet)
                                appArpLines.Add($"    Set-ADTRegistryKey -LiteralPath \"{litPath}\" -Name \"NoModify\" -Value \"1\" -Type \"DWord\"");
                            else
                                log.AppendLine($"  NoModify already set in registry for {litPath} - skipped");
                        }
                        if (doNoRepair)
                        {
                            bool alreadySet = liveKey?.GetValue("NoRepair") != null;
                            if (!alreadySet)
                                appArpLines.Add($"    Set-ADTRegistryKey -LiteralPath \"{litPath}\" -Name \"NoRepair\" -Value \"1\" -Type \"DWord\"");
                            else
                                log.AppendLine($"  NoRepair already set in registry for {litPath} - skipped");
                        }
                        if (doNoRemove)
                        {
                            bool alreadySet = liveKey?.GetValue("NoRemove") != null;
                            if (!alreadySet)
                                appArpLines.Add($"    Set-ADTRegistryKey -LiteralPath \"{litPath}\" -Name \"NoRemove\" -Value \"1\" -Type \"DWord\"");
                            else
                                log.AppendLine($"  NoRemove already set in registry for {litPath} - skipped");
                        }

                        liveKey?.Dispose();

                        if (appArpLines.Count > 0)
                        {
                            string perAppLabel = kvp.Value;
                            if (string.IsNullOrWhiteSpace(perAppLabel)) perAppLabel = arpFallbackLabel;
                            string perAppComment = string.IsNullOrWhiteSpace(perAppLabel)
                                ? "# ARP flags - prevent Modify/Repair/Remove from Add/Remove Programs"
                                : $"# ARP flags - {perAppLabel}";
                            arpLines.Add("    " + perAppComment);
                            arpLines.AddRange(appArpLines);
                            arpLines.Add(""); // blank line between apps
                        }
                    }

                    // Remove trailing blank lines then add exactly one
                    while (arpLines.Count > 0 && arpLines[arpLines.Count - 1] == "")
                        arpLines.RemoveAt(arpLines.Count - 1);
                    arpLines.Add("");

                    if (arpLines.Count > 1)
                    {
                        // Filter out per-app blocks whose registry key path is already in the script
                        var filteredArpLines = new System.Collections.Generic.List<string>();
                        var blockComment  = new System.Collections.Generic.List<string>();
                        var blockSetLines = new System.Collections.Generic.List<string>();

                        void FlushBlock()
                        {
                            if (blockSetLines.Count == 0) { blockComment.Clear(); return; }
                            bool allPresent = blockSetLines.All(l =>
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(
                                    l, @"-LiteralPath\s+""([^""]+)""",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                return m.Success && content.Contains(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
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
                                blockSetLines.Add(line);
                        }
                        FlushBlock();

                        while (filteredArpLines.Count > 0 && filteredArpLines[filteredArpLines.Count - 1] == "")
                            filteredArpLines.RemoveAt(filteredArpLines.Count - 1);

                        if (filteredArpLines.Count > 0)
                        {
                            filteredArpLines.Add("");
                            content = InjectAfterComment(content, POST_MARKER,
                                string.Join(Environment.NewLine, filteredArpLines), log);
                            log.AppendLine("  Injected ARP flag command(s) after Post-Installation marker");
                        }
                        else
                            log.AppendLine("  ARP flags already present in script for all apps - skipped");
                    }
                }
                else
                    log.AppendLine("  ARP flags: no matching uninstall registry entry found - skipped");
            }

            File.WriteAllText(scriptPath, content, new UTF8Encoding(true));
        }


        // ── Patch a named parameter — handles BOTH formats: ─────────────
        //   Hashtable: AppName = 'old'     (PSADT v4 $adtSession = @{ ... })
        //   Switch:    -AppName 'old'      (PSADT v3 Open-ADTSession)
        private static string PatchSessionParam(string content, string paramName,
            string value, StringBuilder log)
        {
            // Format 1: hashtable assignment  "    AppName = 'something'"
            var htPattern = new System.Text.RegularExpressions.Regex(
                $@"(?m)^(\s*{System.Text.RegularExpressions.Regex.Escape(paramName)}\s*=\s*).*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (htPattern.IsMatch(content))
            {
                log.AppendLine($"  Updated {paramName} = {value} (hashtable style).");
                return htPattern.Replace(content, $"${{1}}{value}", 1);
            }

            // Format 2: switch parameter  "-AppName 'something'"
            var swPattern = new System.Text.RegularExpressions.Regex(
                $@"-{System.Text.RegularExpressions.Regex.Escape(paramName)}\s+[^\r\n]+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (swPattern.IsMatch(content))
            {
                log.AppendLine($"  Updated -{paramName} {value} (switch style).");
                return swPattern.Replace(content, $"-{paramName} {value}", 1);
            }

            log.AppendLine($"  ⚠ {paramName} not found in script - skipped.");
            return content;
        }

        // ── Inject a line immediately after a comment marker ─────────────
        private static string InjectAfterComment(string content, string commentMarker,
            string lineToInject, StringBuilder log)
        {
            var lines = content.Split('\n').ToList();
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].TrimEnd().EndsWith(commentMarker.TrimStart('#').Trim(),
                        StringComparison.OrdinalIgnoreCase) ||
                    lines[i].Contains(commentMarker, StringComparison.OrdinalIgnoreCase))
                {
                    // Insert a blank line first, then the block — so there is always
                    // one empty line between the ## marker and the first injected command.
                    lines.Insert(i + 1, lineToInject);
                    lines.Insert(i + 1, "");
                    log.AppendLine($"  Injected after '{commentMarker}' at line {i + 1}.");
                    return string.Join("\n", lines);
                }
            }
            log.AppendLine($"  ⚠ Marker not found: '{commentMarker}' - line not injected.");
            return content;
        }

        // ══════════════════════════════════════════════════════════════════
        //  RUN IntuneWinAppUtil.exe
        // ══════════════════════════════════════════════════════════════════
        private string RunIntuneWinAppUtil(string sourceFolder, string setupFile,
            string outputFolder, StringBuilder log,
            System.Threading.CancellationToken token = default,
            Action<System.Diagnostics.Process>? onProcessStarted = null)
        {
            // Restored to original working pattern (ReadToEnd → WaitForExit).
            // Cancel is handled by a watchdog thread that kills the process if token fires.
            // Never throws OperationCanceledException — returns "" on cancel or failure.
            string args = $"-c \"{sourceFolder}\" -s \"{setupFile}\" -o \"{outputFolder}\" -q";
            log.AppendLine($"  IntuneWinAppUtil: {args}");

            if (token.IsCancellationRequested) return "";

            var psi = new ProcessStartInfo
            {
                FileName               = IntuneWinAppUtilPath,
                Arguments              = args,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };

            using var proc = Process.Start(psi)!;
            onProcessStarted?.Invoke(proc);

            // Watchdog: if token is cancelled BEFORE the process finishes, kill it.
            // Uses a linked CTS so we can stop the watchdog cleanly when process exits
            // normally — prevents the watchdog firing on CTS.Dispose() after completion.
            bool cancelled = false;
            using var watchdogCts = new System.Threading.CancellationTokenSource();
            var watchdog = System.Threading.Tasks.Task.Run(() =>
            {
                // Wait for either: user cancel (token) or process-done signal (watchdogCts)
                int idx = System.Threading.WaitHandle.WaitAny(new[]
                {
                    token.WaitHandle,
                    watchdogCts.Token.WaitHandle
                });
                if (idx == 0 && !watchdogCts.IsCancellationRequested)
                {
                    // User cancel fired first — kill the process
                    cancelled = true;
                    try { if (!proc.HasExited) proc.Kill(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
            });

            // Original working pattern — ReadToEnd blocks until process exits + pipes close.
            string stdout = "", stderr = "";
            try { stdout = proc.StandardOutput.ReadToEnd(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            try { stderr = proc.StandardError.ReadToEnd(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            proc.WaitForExit();

            // Signal watchdog that process finished normally — stop it before it fires
            watchdogCts.Cancel();
            watchdog.Wait();

            onProcessStarted?.Invoke(null!); // clears _activeIntuneProc in caller

            if (cancelled)
            {
                log.AppendLine("  .intunewin creation cancelled.");
                return "";
            }

            if (!string.IsNullOrWhiteSpace(stdout)) log.AppendLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) log.AppendLine(stderr);

            if (proc.ExitCode != 0)
            {
                log.AppendLine($"  ⚠ IntuneWinAppUtil exited with code {proc.ExitCode}");
                return "";
            }

            // IntuneWinAppUtil creates <SetupFile>.intunewin
            string baseName     = IOPath.GetFileNameWithoutExtension(setupFile);
            string expectedFile = IOPath.Combine(outputFolder, baseName + ".intunewin");
            if (File.Exists(expectedFile))
            {
                log.AppendLine($"  .intunewin: {expectedFile}");
                return expectedFile;
            }
            // Fallback: first .intunewin in output folder
            string? fallback = Directory.GetFiles(outputFolder, "*.intunewin").FirstOrDefault();
            log.AppendLine(fallback != null
                ? $"  .intunewin (fallback): {fallback}"
                : "  ⚠ .intunewin not found after tool run");
            return fallback ?? "";
        }

        // ══════════════════════════════════════════════════════════════════
        //  PUBLISH TO INTUNE  (Graph API — mirrors PublishDeployPage)









        // ══════════════════════════════════════════════════════════════════
        //  POWERSHELL HELPER
        // ══════════════════════════════════════════════════════════════════
        private static void RunPsScript(string scriptContent, StringBuilder log, bool isScript = false)
        {
            string tmp = IOPath.Combine(IOPath.GetTempPath(), $"sp_auto_{Guid.NewGuid():N}.ps1");
            try
            {
                File.WriteAllText(tmp, scriptContent, new UTF8Encoding(false));
                var psi = new ProcessStartInfo
                {
                    FileName               = "powershell.exe",
                    Arguments              = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tmp}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                };
                using var proc = Process.Start(psi)!;
                string stdout  = proc.StandardOutput.ReadToEnd();
                string stderr  = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (!string.IsNullOrWhiteSpace(stdout)) log.AppendLine($"  PS: {stdout.Trim()}");
                if (!string.IsNullOrWhiteSpace(stderr)) log.AppendLine($"  PS err: {stderr.Trim()}");
            }
            finally { try { File.Delete(tmp); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } }
        }

        // ══════════════════════════════════════════════════════════════════
        //  EXE TAB — state and handlers
        // ══════════════════════════════════════════════════════════════════
        private string _exePath          = "";
        private string _exeUninstallPath = "";
        private int    _exeLoadGen       = 0;   // incremented on every new EXE load; guards stale background callbacks

        // ── Install Source browse / drag-drop ─────────────────────────────
        private void ExeBtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Select Install EXE",
                Filter = "Executable (*.exe)|*.exe|All Files (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return;
            ExeTxtSource.Text = dlg.FileName;
        }

        private void ExeBtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)!).Handle;
            string? picked = ShellFolderPicker.Browse(hwnd, "Select Source Folder", @"C:\");
            if (picked != null) ExeTxtSource.Text = picked;
        }

        private void ExeBtnCancelSource_Click(object sender, RoutedEventArgs e)
            => ExeTxtSource.Clear();

        // ── Uninstall Source browse / drag-drop ───────────────────────────
        private void ExeBtnBrowseUninstallFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Select Uninstall EXE",
                Filter = "Executable (*.exe)|*.exe|All Files (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return;
            ExeTxtUninstallSource.Text = dlg.FileName;
            _exeUninstallPath = dlg.FileName;
            ExeRebuildCommands();
        }

        private void ExeBtnBrowseUninstallFolder_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)!).Handle;
            string? picked = ShellFolderPicker.Browse(hwnd, "Select Uninstall Source Folder", @"C:\");
            if (picked != null) { ExeTxtUninstallSource.Text = picked; _exeUninstallPath = picked; ExeRebuildCommands(); }
        }

        private void ExeBtnCancelUninstallSource_Click(object sender, RoutedEventArgs e)
        {
            ExeTxtUninstallSource.Clear();
            _exeUninstallPath = "";
            ExeRebuildCommands();
        }

        private void ExeUninstallSource_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void ExeUninstallSource_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files?.Length > 0)
            {
                ExeTxtUninstallSource.Text = files[0];
                _exeUninstallPath = files[0];
                ExeRebuildCommands();
            }
            e.Handled = true;
        }

        private void ExeBtnBrowseTemplate_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)!).Handle;
            string? picked = ShellFolderPicker.Browse(hwnd, "Select Template Folder", @"C:\");
            if (picked != null) ExeTxtTemplateFolder.Text = picked;
        }

        private void ExeBtnClearTemplate_Click(object sender, RoutedEventArgs e)
        {
            ExeTxtTemplateFolder?.Clear();
            ExeRebuildCommands();
        }

        private void ExeTxtSource_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void ExeTxtSource_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files?.Length > 0) ExeTxtSource.Text = files[0];
            e.Handled = true;
        }

        // ── Source changed ────────────────────────────────────────────────
        private void ExeTxtSource_TextChanged(object sender, TextChangedEventArgs e)
        {
            ExeTxtSourceError.Visibility = Visibility.Collapsed;
            string src = ExeTxtSource.Text.Trim();
            // Sync header textbox
            if (HdrTxtSource != null && HdrTxtSource.Text != src)
            {
                HdrTxtSource.TextChanged -= HdrTxtSource_TextChanged;
                HdrTxtSource.Text = src;
                HdrTxtSource.TextChanged += HdrTxtSource_TextChanged;
            }
            if (string.IsNullOrWhiteSpace(src)) { ExeClearFields(); return; }
            _sourceFullyLoaded = false; // reset - will be set when EXE props are applied

            string resolved = "";
            var exeExtensions = new[] { ".exe", ".ps1", ".bat", ".cmd" };
            if (File.Exists(src) && exeExtensions.Any(ext =>
                    src.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            {
                resolved = src;
            }
            else if (Directory.Exists(src))
            {
                // When a FOLDER is browsed, search AllDirectories for the primary installer
                // so all fields (Name, Version, Publisher, Detection, etc.) are auto-populated
                // exactly like Create Package does — mirroring AutoPopulateMainSourceFile.
                // IMPORTANT: resolved is used ONLY for field population (FileVersionInfo /
                // registry lookup).  ExeTxtSource.Text stays as the folder path so that
                // BtnCreate_Exe uses CopyDirectory(folder) and copies ALL contents, not just
                // the single resolved file.
                //
                // Priority: .exe at root → .msi at root → .exe in subdirs → .msi in subdirs
                // → .ps1/.cmd at root → .ps1/.cmd in subdirs
                resolved =
                    Directory.GetFiles(src, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault()
                 ?? Directory.GetFiles(src, "*.msi", SearchOption.TopDirectoryOnly).FirstOrDefault()
                 ?? Directory.GetFiles(src, "*.exe", SearchOption.AllDirectories).FirstOrDefault()
                 ?? Directory.GetFiles(src, "*.msi", SearchOption.AllDirectories).FirstOrDefault()
                 ?? Directory.GetFiles(src, "*.ps1", SearchOption.TopDirectoryOnly).FirstOrDefault()
                 ?? Directory.GetFiles(src, "*.cmd", SearchOption.TopDirectoryOnly).FirstOrDefault()
                 ?? Directory.GetFiles(src, "*.ps1", SearchOption.AllDirectories).FirstOrDefault()
                 ?? "";
            }

            if (!string.IsNullOrEmpty(resolved))
            {
                // Always fully reset before loading new EXE — same logic as MSI tab.
                // Prevents previous file's data (name, version, detection, etc.) from
                // persisting when the user rebrowses to a different EXE.
                if (!string.Equals(resolved, _exePath, StringComparison.OrdinalIgnoreCase))
                {
                    ExeClearFields();
                }
                _exePath = resolved;
                int myGen = ++_exeLoadGen;   // snapshot - background thread uses this to detect staleness

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ExeTxtInstallArgs   != null) ExeTxtInstallArgs.Clear();
                    if (ExeTxtUninstallArgs != null) ExeTxtUninstallArgs.Clear();
                }));

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    // 1. Read basic info from FileVersionInfo (fast, always works)
                    string exeName = "", vendor = "", version = "";
                    try
                    {
                        var vi = FileVersionInfo.GetVersionInfo(resolved);
                        vendor  = vi.CompanyName  ?? "";
                        exeName = vi.ProductName  ?? IOPath.GetFileNameWithoutExtension(resolved);
                        version = vi.ProductVersion ?? vi.FileVersion ?? "";
                    }
                    catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                    // 2. Try registry lookup — more accurate for installed apps
                    var reg = LookupInstalledByExe(resolved, exeName);
                    if (reg != null)
                    {
                        if (!string.IsNullOrWhiteSpace(reg.DisplayName)) exeName = reg.DisplayName;
                        if (!string.IsNullOrWhiteSpace(reg.Publisher))   vendor  = reg.Publisher;
                        // Use DisplayVersion from registry — if registry entry found but DisplayVersion
                        // is absent/empty, clear version (do NOT fall back to FileVersionInfo or MainExeVersion).
                        // This matches the registry exactly: "Duplicate Cleaner_is1" has no DisplayVersion → blank.
                        version = reg.DisplayVersion?.Trim() ?? "";
                    }

                    // Last-resort fallback: if still no name/version, use the filename as name
                    if (string.IsNullOrWhiteSpace(exeName))
                        exeName = IOPath.GetFileNameWithoutExtension(resolved);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // Stale check: if the user has already browsed a newer EXE, discard this result.
                        if (myGen != _exeLoadGen) return;
                        // Only apply registry/FileVersionInfo values when the field has NOT already
                        // been populated from the per-app JSON or Download tab (pending metadata).
                        // Per-app JSON / Download tab values are the authoritative source; registry
                        // is a fallback for when no JSON data is available.
                        if (!string.IsNullOrWhiteSpace(vendor)  && string.IsNullOrWhiteSpace(ExeTxtPublisher.Text))
                            ExeTxtPublisher.Text  = vendor.Trim();
                        if (!string.IsNullOrWhiteSpace(exeName) && string.IsNullOrWhiteSpace(ExeTxtAppName.Text))
                            ExeTxtAppName.Text    = exeName.Trim();
                        if (!string.IsNullOrWhiteSpace(version) && string.IsNullOrWhiteSpace(ExeTxtAppVersion.Text))
                            ExeTxtAppVersion.Text = version.Trim();
                        if (string.IsNullOrWhiteSpace(ExeTxtAppRevision.Text))
                            ExeTxtAppRevision.Text = "01";

                        // Populate uninstall source + args.
                        // Priority: per-app JSON UninstallPath → registry UninstallString.
                        // Per-app JSON is always preferred even when the app is installed.
                        {
                            string jsonUninstallPath = _staticPendingPackageTemplateFolder != ""
                                ? TryReadJsonUninstallPath(_staticPendingPackageTemplateFolder)
                                : TryReadJsonUninstallPath(_pendingTemplateFolder);
                            string jsonUninstallArgs = _staticPendingPackageTemplateFolder != ""
                                ? TryReadJsonUninstallArgs(_staticPendingPackageTemplateFolder)
                                : TryReadJsonUninstallArgs(_pendingTemplateFolder);

                            if (!string.IsNullOrWhiteSpace(jsonUninstallPath))
                            {
                                // JSON has UninstallPath — use it directly, ignore registry
                                _exeUninstallPath = jsonUninstallPath;
                                if (ExeTxtUninstallSource != null)
                                    ExeTxtUninstallSource.Text = jsonUninstallPath;
                                if (!string.IsNullOrWhiteSpace(jsonUninstallArgs) &&
                                    ExeTxtUninstallArgs != null &&
                                    string.IsNullOrWhiteSpace(ExeTxtUninstallArgs.Text))
                                    ExeTxtUninstallArgs.Text = jsonUninstallArgs;
                            }
                            else if (reg != null)
                            {
                                // JSON has no UninstallPath — fall back to registry
                                string uninstStr = !string.IsNullOrWhiteSpace(reg.QuietUninstallString)
                                    ? reg.QuietUninstallString
                                    : reg.UninstallString;

                                if (!string.IsNullOrWhiteSpace(uninstStr))
                                {
                                    string uExe = "", uArgs = "";
                                    if (uninstStr.StartsWith("\""))
                                    {
                                        int end = uninstStr.IndexOf('"', 1);
                                        if (end > 1)
                                        {
                                            uExe  = uninstStr.Substring(1, end - 1).Trim();
                                            uArgs = uninstStr.Substring(end + 1).Trim();
                                        }
                                    }
                                    else
                                    {
                                        var parts = uninstStr.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                                        uExe  = parts.Length > 0 ? parts[0] : "";
                                        uArgs = parts.Length > 1 ? parts[1] : "";
                                    }
                                    if (!string.IsNullOrWhiteSpace(uExe) && File.Exists(uExe) &&
                                        ExeTxtUninstallSource != null)
                                    {
                                        ExeTxtUninstallSource.Text = uExe;
                                        _exeUninstallPath = uExe;
                                    }
                                    if (!string.IsNullOrWhiteSpace(uArgs) &&
                                        ExeTxtUninstallArgs != null &&
                                        string.IsNullOrWhiteSpace(ExeTxtUninstallArgs.Text))
                                        ExeTxtUninstallArgs.Text = uArgs;
                                }
                            }
                        }

                        if (reg != null)
                        {


                                // fall back to DisplayVersion / FileVersionInfo version
                                string detVer = !string.IsNullOrWhiteSpace(reg.MainExeVersion)
                                    ? reg.MainExeVersion
                                    : (!string.IsNullOrWhiteSpace(reg.DisplayVersion) ? reg.DisplayVersion : version);
                                // detection value not shown (controls removed)

                            // Registry detection — standard uninstall hive path (controls removed)

                            // Close Applications — auto-populate from main exe name
                            if (ExeTxtCloseApps != null && string.IsNullOrWhiteSpace(ExeTxtCloseApps.Text))
                            {
                                string closeApp = DeriveCloseApps(reg);
                                if (!string.IsNullOrWhiteSpace(closeApp))
                                    ExeTxtCloseApps.Text = closeApp;
                            }
                        }

                        // Re-apply detection method + operator from config

                        // ── Install + Uninstall args/path from per-app JSON ───────────────
                        // Read SilentInstallParameterEXE and UninstallPath from the per-app
                        // JSON. Per-app JSON always takes priority over the built-in DB lookup
                        // and registry, even when the app is installed.
                        {
                            try
                            {
                                string jsonFolder = !string.IsNullOrWhiteSpace(_staticPendingPackageTemplateFolder)
                                    ? _staticPendingPackageTemplateFolder
                                    : _pendingTemplateFolder;
                                if (!string.IsNullOrWhiteSpace(jsonFolder) && Directory.Exists(jsonFolder))
                                {
                                    string? jsonPath = Directory.GetFiles(jsonFolder, "*.json",
                                        SearchOption.TopDirectoryOnly).FirstOrDefault();
                                    if (jsonPath != null)
                                    {
                                        var j = System.Text.Json.JsonSerializer.Deserialize<AutoPackageAppEntry>(
                                            File.ReadAllText(jsonPath),
                                            new System.Text.Json.JsonSerializerOptions
                                                { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true });

                                        // SilentInstallParameterEXE → _jsonInstallArgs (persists for ExeRebuildCommands)
                                        string jInstArgs = j?.SilentInstallParameterEXE?.Trim() ?? "";
                                        if (!string.IsNullOrWhiteSpace(jInstArgs))
                                            _jsonInstallArgs = jInstArgs;

                                        // UninstallPath + SilentUninstallParameterEXE
                                        string jUninstPath = j?.UninstallPath?.Trim() ?? "";
                                        string jUninstArgs = j?.SilentUninstallParameterEXE?.Trim() ?? "";
                                        if (!string.IsNullOrWhiteSpace(jUninstPath))
                                        {
                                            _exeUninstallPath = jUninstPath;
                                            if (ExeTxtUninstallSource != null)
                                                ExeTxtUninstallSource.Text = jUninstPath;
                                            if (!string.IsNullOrWhiteSpace(jUninstArgs) &&
                                                ExeTxtUninstallArgs != null &&
                                                string.IsNullOrWhiteSpace(ExeTxtUninstallArgs.Text))
                                                ExeTxtUninstallArgs.Text = jUninstArgs;
                                        }
                                    }
                                }
                            }
                            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        }

                        // ── Uninstall path from registry (fallback when JSON has no UninstallPath) ──

                        ExeRebuildPackageName();
                        ExeRebuildCommands();
                        _sourceFullyLoaded = true; // EXE props fully applied - safe to trigger Create
                    }));
                });
            }
        }


        // ── Read UninstallPath from per-app JSON ───────────────────────────
        private static string TryReadJsonUninstallPath(string jsonFolder)
            => TryReadJsonField(jsonFolder, "UninstallPath");

        private static string TryReadJsonUninstallArgs(string jsonFolder)
            => TryReadJsonField(jsonFolder, "SilentUninstallParameterEXE");

        private static string TryReadJsonField(string jsonFolder, string field)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jsonFolder) || !Directory.Exists(jsonFolder)) return "";
                string? jsonPath = Directory.GetFiles(jsonFolder, "*.json", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (jsonPath == null) return "";
                var j = System.Text.Json.JsonSerializer.Deserialize<AutoPackageAppEntry>(
                    File.ReadAllText(jsonPath),
                    new System.Text.Json.JsonSerializerOptions
                        { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true });
                return field switch
                {
                    "UninstallPath"              => j?.UninstallPath?.Trim()              ?? "",
                    "SilentUninstallParameterEXE"=> j?.SilentUninstallParameterEXE?.Trim() ?? "",
                    _                            => ""
                };
            }
            catch { return ""; }
        }

        // ══════════════════════════════════════════════════════════════════
        // LOOKUP INSTALLED APP FROM REGISTRY BY EXE PATH OR NAME
        // Searches HKLM 64-bit, HKLM WOW6432Node, and HKCU uninstall hives.
        // Returns null if not found.
        // ══════════════════════════════════════════════════════════════════
        private record ExeInstalledInfo(
            string DisplayName,
            string Publisher,
            string DisplayVersion,
            string InstallLocation,
            string MainExePath,
            string MainExeVersion,
            string UninstallString,
            string QuietUninstallString,
            string RegKeyPath,
            bool   Is32Bit);

        private static ExeInstalledInfo? LookupInstalledByExe(string exePath, string productNameHint)
        {
            // Mirrors CreatePackagePage.LookupInstalledByExe exactly.
            // Searches HKLM 64-bit, HKLM WOW6432Node (is32=true), HKCU uninstall hives.
            var hives = new (Microsoft.Win32.RegistryKey root, string sub, bool is32)[]
            {
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false),
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", true),
                (Microsoft.Win32.Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false),
            };

            ExeInstalledInfo? bestMatch = null;
            int bestScore = 0;

            // Pre-compute normalised EXE filename token (strip version/arch suffixes)
            string exeBaseName = IOPath.GetFileNameWithoutExtension(exePath);
            string exeStripped = System.Text.RegularExpressions.Regex.Replace(
                exeBaseName,
                @"[-_.\s]?(setup|install|installer|win\d+|x64|x86|\d[\d.\-]*).*$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            string exeNorm = System.Text.RegularExpressions.Regex.Replace(exeStripped, @"[\s\-_.]", "").ToLowerInvariant();

            // Significant words: ≥3 chars, not digits-only, not common noise words
            static string[] SigWords(string s) => s.Split(new[]{' ','-','_'}, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3
                    && !System.Text.RegularExpressions.Regex.IsMatch(w, @"^\d[\d.]*$")
                    && !new[]{"setup","install","installer","free","trial","version","portable"}.Contains(w.ToLowerInvariant()))
                .Select(w => w.ToLowerInvariant()).ToArray();

            string[] hintWords = SigWords(productNameHint);

            foreach (var (root, subPath, is32) in hives)
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

                            // Score 10: DisplayIcon path exactly matches browsed EXE
                            if (!string.IsNullOrWhiteSpace(exePath) && !string.IsNullOrWhiteSpace(dispIcon))
                            {
                                if (string.Equals(dispIcon, exePath, StringComparison.OrdinalIgnoreCase))
                                    score = 10;
                                else if (string.Equals(IOPath.GetDirectoryName(dispIcon),
                                             IOPath.GetDirectoryName(exePath),
                                             StringComparison.OrdinalIgnoreCase))
                                    score = 6;
                            }

                            // Score 8: exact DisplayName == productNameHint
                            // Score 5: significant word overlap (≥2 shared words, ≥ half of hints)
                            // Score 4: one name contains the other
                            if (score == 0 && !string.IsNullOrWhiteSpace(productNameHint))
                            {
                                if (string.Equals(dispName, productNameHint, StringComparison.OrdinalIgnoreCase))
                                    score = 8;
                                else if (dispName.Contains(productNameHint, StringComparison.OrdinalIgnoreCase) ||
                                         productNameHint.Contains(dispName, StringComparison.OrdinalIgnoreCase))
                                    score = 4;
                                else if (hintWords.Length >= 2)
                                {
                                    string dispNameLow = dispName.ToLowerInvariant();
                                    int shared = hintWords.Count(w => dispNameLow.Contains(w));
                                    if (shared >= 2 && shared >= hintWords.Length / 2)
                                        score = 5;
                                }
                            }

                            // Score 4: EXE filename (normalised) matches DisplayName
                            // Requires ≥3 chars on both sides (not ≥4) to catch short names like "fc".
                            // Uses word-boundary check for short names: "fc" must appear as a complete
                            // word in DisplayName (e.g. "FC file compare") to avoid one and two
                            // character false hits.
                            if (score == 0 && exeNorm.Length >= 3)
                            {
                                string dispNameNorm  = System.Text.RegularExpressions.Regex.Replace(dispName, @"[\s\-_.]", "").ToLowerInvariant();
                                string dispFirstNorm = System.Text.RegularExpressions.Regex.Replace(
                                    dispName.Split(' ')[0], @"[\s\-_.]", "").ToLowerInvariant();

                                bool nameMatch = false;
                                if (exeNorm.Length >= 4)
                                {
                                    // Original logic for longer names
                                    nameMatch = dispFirstNorm.Length >= 3 &&
                                        (dispNameNorm.Contains(exeNorm, StringComparison.OrdinalIgnoreCase) ||
                                         exeNorm.Contains(dispFirstNorm, StringComparison.OrdinalIgnoreCase));
                                }
                                else
                                {
                                    // Short name (3 chars): require whole-word match in DisplayName
                                    // e.g. "fc" matches "FC file compare" but NOT "effective"
                                    string dispNameLower = dispName.ToLowerInvariant();
                                    nameMatch = System.Text.RegularExpressions.Regex.IsMatch(
                                        dispNameLower,
                                        @"(^|[\s\-_(])" + System.Text.RegularExpressions.Regex.Escape(exeNorm) + @"($|[\s\-_)])",
                                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                }
                                if (nameMatch) score = 4;
                            }

                            if (score <= bestScore) continue;

                            string dispVer       = key.GetValue("DisplayVersion")      as string ?? "";
                            string publisher     = key.GetValue("Publisher")            as string ?? "";
                            string instLoc       = (key.GetValue("InstallLocation")    as string ?? "").TrimEnd(IOPath.DirectorySeparatorChar);
                            string uninstStr     = key.GetValue("UninstallString")      as string ?? "";
                            string quietUninst   = key.GetValue("QuietUninstallString") as string ?? "";

                            // Derive install folder from DisplayIcon or UninstallString if InstallLocation empty
                            if (string.IsNullOrWhiteSpace(instLoc) && dispIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                instLoc = (IOPath.GetDirectoryName(dispIcon) ?? "").TrimEnd(IOPath.DirectorySeparatorChar);
                            if (string.IsNullOrWhiteSpace(instLoc) && !string.IsNullOrWhiteSpace(uninstStr))
                            {
                                string uPath = uninstStr.Trim().Trim('"');
                                if (uPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(uPath))
                                    instLoc = (IOPath.GetDirectoryName(uPath) ?? "").TrimEnd(IOPath.DirectorySeparatorChar);
                            }

                            // Find main EXE: prefer DisplayIcon, then scan install folder
                            // score each candidate by how many DisplayName sig-words appear in its filename
                            string mainExePath = "";
                            if (!string.IsNullOrWhiteSpace(dispIcon) &&
                                dispIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(dispIcon))
                            {
                                mainExePath = dispIcon;
                            }
                            else if (!string.IsNullOrWhiteSpace(instLoc) && Directory.Exists(instLoc))
                            {
                                string[] dispWords = SigWords(dispName);
                                var exes = Directory.GetFiles(instLoc, "*.exe", SearchOption.TopDirectoryOnly)
                                    .Where(f => !IOPath.GetFileNameWithoutExtension(f)
                                        .StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                                    .ToArray();
                                if (exes.Length > 0)
                                    mainExePath = exes
                                        .OrderByDescending(f => { string fn = IOPath.GetFileNameWithoutExtension(f).ToLowerInvariant(); return dispWords.Count(w => fn.Contains(w)); })
                                        .ThenByDescending(f => new FileInfo(f).Length)
                                        .First();
                            }
                            if (string.IsNullOrWhiteSpace(mainExePath)) mainExePath = exePath;
                            // Last resort: scan Start Menu shortcuts for an exe matching the app name
                            if (string.IsNullOrWhiteSpace(mainExePath) || !File.Exists(mainExePath))
                            {
                                string shortcutExe = ResolveMainExeFromShortcuts(dispName);
                                if (!string.IsNullOrWhiteSpace(shortcutExe)) mainExePath = shortcutExe;
                            }

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
                                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                            }

                            string hiveRoot = root == Microsoft.Win32.Registry.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
                            bestScore = score;
                            bestMatch = new ExeInstalledInfo(
                                dispName, publisher, dispVer, instLoc,
                                mainExePath, mainExeVer, uninstStr, quietUninst,
                                hiveRoot + "\\" + subPath + "\\" + keyName, is32);
                        }
                        catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    }
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            // Threshold ≥4 — same as CreatePackage: prevents low-confidence matches
            return bestScore >= 4 ? bestMatch : null;
        }

        // ══════════════════════════════════════════════════════════════════
        // LOOKUP INSTALLED APP BY MSI PRODUCT CODE
        // ══════════════════════════════════════════════════════════════════
        private static ExeInstalledInfo? LookupInstalledByProductCode(string productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode)) return null;
            string pc = productCode.Trim();
            if (!pc.StartsWith("{")) pc = "{" + pc;
            if (!pc.EndsWith("}"))   pc += "}";

            var hives = new (Microsoft.Win32.RegistryKey root, string sub, bool is32)[]
            {
                (Microsoft.Win32.Registry.LocalMachine,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false),
                (Microsoft.Win32.Registry.LocalMachine,
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", true),
                (Microsoft.Win32.Registry.CurrentUser,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false),
            };

            foreach (var (root, subPath, is32) in hives)
            {
                try
                {
                    using var hive = root.OpenSubKey(subPath);
                    if (hive == null) continue;
                    using var key  = hive.OpenSubKey(pc);
                    if (key == null) continue;

                    string hiveRoot    = root == Microsoft.Win32.Registry.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
                    string regKeyPath  = hiveRoot + "\\" + subPath + "\\" + pc;
                    string dispName    = key.GetValue("DisplayName")          as string ?? "";
                    string publisher   = key.GetValue("Publisher")            as string ?? "";
                    string dispVer     = key.GetValue("DisplayVersion")       as string ?? "";
                    string instLoc     = (key.GetValue("InstallLocation")     as string ?? "").TrimEnd(IOPath.DirectorySeparatorChar);
                    string dispIcon    = (key.GetValue("DisplayIcon")         as string ?? "").Split(',')[0].Trim().Trim('"');
                    string uninstStr   = key.GetValue("UninstallString")      as string ?? "";
                    string quietUnin   = key.GetValue("QuietUninstallString") as string ?? "";

                    if (string.IsNullOrWhiteSpace(instLoc) && dispIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        instLoc = (IOPath.GetDirectoryName(dispIcon) ?? "").TrimEnd(IOPath.DirectorySeparatorChar);

                    // Find main exe: DisplayIcon if .exe > largest exe in install folder > Start Menu shortcuts
                    string mainExePath = "", mainExeVer = "";
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
                    // Fallback: scan Start Menu shortcuts for a .lnk pointing to an .exe
                    // that contains the app name (e.g. SharedView shortcut → SharedView.exe)
                    if (string.IsNullOrWhiteSpace(mainExePath))
                        mainExePath = ResolveMainExeFromShortcuts(dispName);
                    if (!string.IsNullOrWhiteSpace(mainExePath) && File.Exists(mainExePath))
                    {
                        try
                        {
                            var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(mainExePath);
                            mainExeVer = (fvi.FileMajorPart > 0 || fvi.FileMinorPart > 0 || fvi.FileBuildPart > 0 || fvi.FilePrivatePart > 0)
                                ? $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}"
                                : fvi.FileVersion?.Trim() ?? "";
                        }
                        catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    }

                    return new ExeInstalledInfo(dispName, publisher, dispVer, instLoc,
                                               mainExePath, mainExeVer, uninstStr, quietUnin, regKeyPath, is32);
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            return null;
        }

        // ── Find the largest .exe in a folder (best proxy for main application exe) ──
        private static string FindLargestExe(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return "";
                var exes = Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly);
                if (exes.Length == 0) return "";
                string best = exes.OrderByDescending(e => new FileInfo(e).Length).First();
                return IOPath.GetFileName(best);
            }
            catch { return ""; }
        }

        // ── Derive close-apps exe name from installed info ─────────────────
        // ── Resolve main exe from Start Menu / Desktop shortcuts ─────────────
        // Scans common shortcut folders for .lnk files whose name contains
        // significant words from the app DisplayName. Returns the resolved exe path.
        private static string ResolveMainExeFromShortcuts(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return "";

            // Executables that are browsers/launchers — never a valid app main exe
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

                // Also try the full display name normalised (no spaces) for matching
                string dispNorm = System.Text.RegularExpressions.Regex.Replace(
                    displayName, @"[\s\-_.]", "").ToLowerInvariant();

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

                // Collect candidates: (score, exePath)
                var candidates = new System.Collections.Generic.List<(int score, string path)>();

                foreach (var folder in folders.Where(f => Directory.Exists(f)))
                {
                    foreach (var lnk in Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories))
                    {
                        string lnkName     = IOPath.GetFileNameWithoutExtension(lnk).ToLowerInvariant();
                        string lnkNameNorm = System.Text.RegularExpressions.Regex.Replace(lnkName, @"[\s\-_.]", "");

                        // Score: how well does shortcut name match the DisplayName?
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

                            string exeBaseName = IOPath.GetFileNameWithoutExtension(target).ToLowerInvariant();

                            // Skip known browsers and launchers
                            if (browserExes.Contains(exeBaseName)) continue;

                            // Prefer targets whose exe name matches a significant word
                            if (sigWords.Any(w => exeBaseName.Contains(w))) matchScore += 5;

                            candidates.Add((matchScore, target));
                        }
                        catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    }
                }

                if (shell != null) try { Marshal.ReleaseComObject(shell); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                if (candidates.Count > 0)
                    return candidates.OrderByDescending(c => c.score).First().path;
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            return "";
        }

        private static string DeriveCloseApps(ExeInstalledInfo? info)
        {
            if (info == null) return "";
            // Prefer MainExePath (.exe files only)
            if (!string.IsNullOrWhiteSpace(info.MainExePath) &&
                info.MainExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return IOPath.GetFileNameWithoutExtension(info.MainExePath);
            // Fallback: scan InstallLocation for largest non-uninstaller exe
            if (!string.IsNullOrWhiteSpace(info.InstallLocation) &&
                Directory.Exists(info.InstallLocation))
            {
                try
                {
                    var best = Directory.GetFiles(info.InstallLocation, "*.exe", SearchOption.TopDirectoryOnly)
                        .Where(f => !IOPath.GetFileNameWithoutExtension(f)
                            .StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => new FileInfo(f).Length)
                        .FirstOrDefault();
                    if (best != null) return IOPath.GetFileNameWithoutExtension(best);
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            return "";
        }

        private void ExeTxtTemplateFolder_TextChanged(object sender, TextChangedEventArgs e)
        {
            ExeUpdateCloseApps();
            ExeRebuildCommands();
        }

        private void ExeAppField_TextChanged(object sender, TextChangedEventArgs e)
        {
            ExeRebuildPackageName();
            ExeRebuildCommands();
        }

        private void ExeArgsField_TextChanged(object sender, TextChangedEventArgs e)
            => ExeRebuildCommands();

        // ── Detection type toggle ─────────────────────────────────────────
        private void ExeDetectTypeChk_Changed(object sender, RoutedEventArgs e)
        {
        }

        private void ExeDetectType_Changed(object sender, RoutedEventArgs e) { }

        private void ExeCmbDetectFileMethod_Changed(object sender, SelectionChangedEventArgs e)
        {
        }

        private void ExeCmbDetectRegMethod_Changed(object sender, SelectionChangedEventArgs e)
        {
        }

        // ── MSI tab detection panel handlers ─────────────────────────────
        private void MsiDetectTypeChk_Changed(object sender, RoutedEventArgs e)
        {
        }

        private void MsiDetectType_Changed(object sender, RoutedEventArgs e) { }

        // ── Detection browse / registry-editor button handlers ────────────────

        private void OpenRegedit(string keyPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(keyPath))
                    using (var applets = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                        applets?.SetValue("LastKey", keyPath);
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName("regedit"))
                    try { proc.Kill(); proc.WaitForExit(1500); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                System.Threading.Thread.Sleep(200);
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("regedit.exe") { UseShellExecute = true });
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        // MSI — File detection
        private void MsiBtnDetectFilePath_Click(object sender, RoutedEventArgs e)
        {
            using var fbd = new System.Windows.Forms.FolderBrowserDialog
                { Description = "Select install folder", UseDescriptionForTitle = true };
        }

        private void MsiBtnDetectFileName_Click(object sender, RoutedEventArgs e)
        {
            // Detection controls removed — handler is a no-op
        }

        // MSI — Registry detection
        private void MsiBtnDetectRegKey_Click(object sender, RoutedEventArgs e)
            => OpenRegedit("");

        private void MsiBtnDetectRegValue_Click(object sender, RoutedEventArgs e)
            => OpenRegedit("");

        // EXE — File detection
        private void ExeBtnDetectFilePath_Click(object sender, RoutedEventArgs e)
        {
            // Detection controls removed — no-op
        }

        private void ExeBtnDetectFileName_Click(object sender, RoutedEventArgs e)
        {
        }

        // EXE — Registry detection
        private void ExeBtnDetectRegKey_Click(object sender, RoutedEventArgs e)
            => OpenRegedit("");

        private void ExeBtnDetectRegValue_Click(object sender, RoutedEventArgs e)
            => OpenRegedit("");

        private void MsiCmbDetectFileMethod_Changed(object sender, SelectionChangedEventArgs e)
        {
        }

        private void MsiCmbDetectRegMethod_Changed(object sender, SelectionChangedEventArgs e)
        {
        }

        // ── Field helpers ─────────────────────────────────────────────────
        private void ExeClearFields()
        {
            _exePath = "";
            _exeUninstallPath = "";
            _jsonInstallArgs  = "";
            ++_exeLoadGen;   // cancel any in-flight background EXE load

            // ── Clear EXE-derived fields ──────────────────────────────────────
            ExeTxtAppName.Clear();
            ExeTxtAppVersion.Clear();
            if (ExeTxtPublisher != null) ExeTxtPublisher.Clear();   // App Vendor
            ExeTxtAppRevision.Clear();
            ExeTxtPackageName.Clear();
            if (ExeTxtCloseApps          != null) ExeTxtCloseApps.Clear();
            if (ExeTxtInstallArgs        != null) ExeTxtInstallArgs.Clear();
            if (ExeTxtUninstallArgs      != null) ExeTxtUninstallArgs.Clear();
            if (ExeTxtUninstallSource    != null) ExeTxtUninstallSource.Clear();

            // ── Restore all JSON-backed fields to config defaults ─────────────
            // Every browse/rebrowse starts from a clean state driven by AutoPackage.json.

            // Template folder
            if (ExeTxtTemplateFolder != null)
                ExeTxtTemplateFolder.Text = _cfg?.TemplateFolderPath ?? "";

            // Combo dropdowns
            _suppressVisibilityUpdate = true;
            _suppressVisibilityUpdate = false;

            // App Vendor (package/app vendor — NOT the Intune "Developer" field).
            string publisher = !string.IsNullOrWhiteSpace(_cfg?.IntuneSettings?.IntuneAppVendor)
                ? _cfg.IntuneSettings.IntuneAppVendor
                : _cfg?.IntuneSettings?.Publisher ?? "";
            ExeTxtPublisher.Text = publisher;

            // Description

            // Group name
            string groupName = !string.IsNullOrWhiteSpace(_cfg?.DeployGroupName)
                ? _cfg.DeployGroupName
                : _cfg?.IntuneSettings?.AssignmentsAvailable ?? "";

            // Install / Uninstall commands — only restore and show when template folder is set
            bool exeTplSet = !string.IsNullOrWhiteSpace(_cfg?.TemplateFolderPath);
            if (ExeTxtInstallCmd   != null) ExeTxtInstallCmd.Text   = exeTplSet ? (_cfg?.IntuneSettings?.InstallCommand   ?? "") : "";
            if (ExeTxtUninstallCmd != null) ExeTxtUninstallCmd.Text = exeTplSet ? (_cfg?.IntuneSettings?.UninstallCommand ?? "") : "";
            UpdateExeCommandRowVisibility(exeTplSet);
        }

        private void ExeRebuildPackageName()
        {
            if (ExeTxtPackageName == null) return;
            string vendor   = Sanitize(ExeTxtPublisher?.Text   ?? "");
            string name     = Sanitize(ExeTxtAppName?.Text     ?? "");
            string version  = Sanitize(ExeTxtAppVersion?.Text  ?? "");
            string revision = Sanitize(ExeTxtAppRevision?.Text ?? "01");
            if (string.IsNullOrEmpty(revision)) revision = "01";
            var parts = new[] { vendor, name, version, revision }.Where(p => !string.IsNullOrEmpty(p));
            ExeTxtPackageName.Text = string.Join("_", parts);
        }

        private void ExeRebuildCommands()
        {
            if (ExeTxtInstallCmd == null || ExeTxtUninstallCmd == null) return;

            string tpl   = ExeTxtTemplateFolder?.Text.Trim() ?? "";
            // Treat any non-empty template folder path as "has template" for command building
            // (directory may not exist yet at browse time)
            bool hasTpl  = !string.IsNullOrWhiteSpace(tpl);

            // When template folder is set: IntuneSettings.json PSADT commands take priority.
            // When NO template: always build direct EXE commands — ignore IntuneSettings commands.
            bool installCmdFromJson2   = hasTpl && !string.IsNullOrWhiteSpace(_cfg?.IntuneSettings?.InstallCommand);
            bool uninstallCmdFromJson2 = hasTpl && !string.IsNullOrWhiteSpace(_cfg?.IntuneSettings?.UninstallCommand);
            string exeName    = string.IsNullOrEmpty(_exePath) ? "" : IOPath.GetFileName(_exePath);

            // Install args priority: _jsonInstallArgs (SilentInstallParameterEXE from per-app JSON,
            // set via SetInstallUninstallArgs, persists across ExeRebuildCommands calls) →
            // per-app JSON file directly → ExeTxtInstallArgs (built-in DB lookup fallback)
            string jsonInstallArgs = !string.IsNullOrWhiteSpace(_jsonInstallArgs)
                ? _jsonInstallArgs
                : TryReadJsonField(
                    !string.IsNullOrWhiteSpace(_staticPendingPackageTemplateFolder)
                        ? _staticPendingPackageTemplateFolder : _pendingTemplateFolder,
                    "SilentInstallParameterEXE");
            string instArgs   = !string.IsNullOrWhiteSpace(jsonInstallArgs)
                ? jsonInstallArgs
                : (ExeTxtInstallArgs?.Text.Trim() ?? "");
            string uninstArgs = ExeTxtUninstallArgs?.Text.Trim() ?? "";

            // Uninstall source: separate path if set, else fall back to install source
            bool hasExternalUninstall = !string.IsNullOrEmpty(_exeUninstallPath);
            string uninstPath = hasExternalUninstall ? _exeUninstallPath : _exePath;
            string uninstName = string.IsNullOrEmpty(uninstPath) ? exeName : IOPath.GetFileName(uninstPath);

            if (hasTpl)
            {
                UpdateExeCommandRowVisibility(true);
                if (!installCmdFromJson2)
                    ExeTxtInstallCmd.Text   = !string.IsNullOrWhiteSpace(_staticSelectedApp?.PSADTInstallCommand)
                        ? _staticSelectedApp.PSADTInstallCommand
                        : "powershell.exe -ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"Invoke-AppDeployToolkit.ps1\" -DeploymentType Install";
                if (!uninstallCmdFromJson2)
                    ExeTxtUninstallCmd.Text = !string.IsNullOrWhiteSpace(_staticSelectedApp?.PSADTUninstallCommand)
                        ? _staticSelectedApp.PSADTUninstallCommand
                        : "powershell.exe -ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"Invoke-AppDeployToolkit.ps1\" -DeploymentType Uninstall";
            }
            else if (!string.IsNullOrEmpty(exeName))
            {
                // No template — show command rows with direct silent install/uninstall commands
                // Install:   <exename> <SilentInstallParameterEXE>   e.g. filecompare-1.0-x64.exe /S
                // Uninstall: <uninstall path> <SilentUninstallParameterEXE>
                UpdateExeCommandRowVisibility(true);
                ExeTxtInstallCmd.Text = string.IsNullOrEmpty(instArgs)
                    ? exeName
                    : $"{exeName} {instArgs}";

                string uninstDisplay = hasExternalUninstall && !string.IsNullOrEmpty(_exeUninstallPath)
                    ? _exeUninstallPath
                    : exeName;
                ExeTxtUninstallCmd.Text = string.IsNullOrEmpty(uninstArgs)
                    ? uninstDisplay
                    : $"{uninstDisplay} {uninstArgs}";
            }
            else
            {
                UpdateExeCommandRowVisibility(true);
                ExeTxtInstallCmd.Clear();
                ExeTxtUninstallCmd.Clear();
            }
        }


        private void ExeUpdateCloseApps()
        {
            if (ExeTxtCloseApps == null) return;
            string tpl    = ExeTxtTemplateFolder?.Text.Trim() ?? "";
            bool hasInvoke = !string.IsNullOrWhiteSpace(tpl) && Directory.Exists(tpl) &&
                             Directory.GetFiles(tpl, "Invoke-AppDeployToolkit.ps1",
                                 SearchOption.AllDirectories).Any();
            ExeTxtCloseApps.IsEnabled = hasInvoke;
            ExeTxtCloseApps.Opacity   = hasInvoke ? 1.0 : 0.45;
            ExeTxtCloseApps.Tag       = "";
        }

        // ── Patch Invoke-AppDeployToolkit.ps1 for EXE ────────────────────
        // ── Patch Invoke-AppDeployToolkit.ps1  (EXE source flow) ─────────
        //  Scans Files\ (all depths) for every .exe and .msi and injects
        //  one command per file — same comment rules as the MSI flow above.
        private static void PatchInvokeScriptForExe(
            string scriptPath, string installExeFullPath, string installArgs,
            string uninstallSource, string uninstallArgs, StringBuilder log,
            string appName = "", string appVendor = "", string appVersion = "",
            bool addMsi = true, bool addMst = true, bool addMsp = true,
            bool addExe = true, bool addBat = true, bool addIss = true,
            bool addReg = true, bool addCer = true, bool addInf = true,
            AutoPackageConfig? cfg = null)
        {
            if (!File.Exists(scriptPath)) return;
            string content  = File.ReadAllText(scriptPath);
            string filesDir = IOPath.Combine(IOPath.GetDirectoryName(scriptPath)!, "Files");

            string RelPath(string full) => full.Substring(filesDir.Length)
                .TrimStart(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);

            // Comment for .exe:
            //   1. Registry (DisplayName, Publisher, DisplayVersion)
            //   2. FileVersionInfo (CompanyName, ProductName, ProductVersion)
            //   3. #filename.exe
            // For the PRIMARY exe (the one the user selected), also try UI field values
            // as an additional fallback before resorting to FileVersionInfo.
            // Build comment + registry info for an EXE file.
            // Returns (comment string, registry UninstallString or "").
            // Priority: registry (using app name hint for accurate matching) →
            //           FileVersionInfo → UI fields → filename.
            (string comment, string uninstStr) ExeInfo(string exePath, string uiName, string uiVendor, string uiVer)
            {
                // Step 1: Registry lookup — use UI app name as hint (same as Create Package).
                // Using the installer filename as hint fails because e.g.
                // "FileCompare Setup 1.0" never matches "Smekal FileCompare (x64 en-US)".
                string nameHint = !string.IsNullOrWhiteSpace(uiName)   ? uiName
                    : !string.IsNullOrWhiteSpace(uiVendor) ? uiVendor
                    : IOPath.GetFileNameWithoutExtension(exePath);
                ExeInstalledInfo? reg = LookupInstalledByExe(exePath, nameHint);
                if (reg != null && !string.IsNullOrWhiteSpace(reg.DisplayName))
                {
                    var rp = new[] { reg.Publisher, reg.DisplayName, reg.DisplayVersion }
                        .Where(p => !string.IsNullOrWhiteSpace(p));
                    string regUninstall = !string.IsNullOrWhiteSpace(reg.QuietUninstallString)
                        ? reg.QuietUninstallString : reg.UninstallString;
                    return ("    # " + string.Join(" ", rp), regUninstall);
                }

                // Step 2: FileVersionInfo from the EXE file itself
                string viVendor = "", viName = "", viVer = "";
                if (File.Exists(exePath))
                {
                    try
                    {
                        var vi = FileVersionInfo.GetVersionInfo(exePath);
                        viVendor = vi.CompanyName?.Trim()  ?? "";
                        viName   = vi.ProductName?.Trim()  ?? "";
                        viVer    = (vi.ProductVersion ?? vi.FileVersion)?.Trim() ?? "";
                    }
                    catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
                string fVendor = !string.IsNullOrWhiteSpace(viVendor) ? viVendor : uiVendor;
                string fName   = !string.IsNullOrWhiteSpace(viName)   ? viName   : uiName;
                string fVer    = !string.IsNullOrWhiteSpace(viVer)    ? viVer    : uiVer;
                var fp = new[] { fVendor, fName, fVer }.Where(p => !string.IsNullOrWhiteSpace(p));
                string c = fp.Any() ? "    # " + string.Join(" ", fp) : "    # " + IOPath.GetFileName(exePath);
                return (c, "");
            }

            // Comment for .msi: registry then MSI COM properties
            string MsiComment(string msiPath)
            {
                var props = ReadMsiPropertiesSTA(msiPath);
                props.TryGetValue("ProductCode", out var pc);
                ExeInstalledInfo? reg = !string.IsNullOrWhiteSpace(pc)
                    ? LookupInstalledByProductCode(pc!) : null;
                if (reg != null)
                {
                    var rp = new[] { reg.Publisher, reg.DisplayName, reg.DisplayVersion }
                        .Where(p => !string.IsNullOrWhiteSpace(p));
                    if (rp.Any()) return "    # " + string.Join(" ", rp);
                }
                props.TryGetValue("Manufacturer",   out var mfr);
                props.TryGetValue("ProductName",    out var pn);
                props.TryGetValue("ProductVersion", out var pv);
                var fp = new[] { mfr, pn, pv }.Where(p => !string.IsNullOrWhiteSpace(p));
                return fp.Any() ? "    # " + string.Join(" ", fp) : "    # " + IOPath.GetFileName(msiPath);
            }

            var installSb   = new System.Text.StringBuilder();
            var uninstallSb = new System.Text.StringBuilder();

            if (Directory.Exists(filesDir))
            {
                string primaryExeName = IOPath.GetFileName(installExeFullPath);

                var allExe = Directory.GetFiles(filesDir, "*.exe", SearchOption.AllDirectories)
                    .OrderBy(f => IOPath.GetDirectoryName(f)!.Length).ThenBy(f => f);
                var allMsi = Directory.GetFiles(filesDir, "*.msi", SearchOption.AllDirectories)
                    .OrderBy(f => IOPath.GetDirectoryName(f)!.Length).ThenBy(f => f);

                // ── EXE install commands ───────────────────────────────────
                foreach (string exePath in allExe)
                {
                    string exeRel  = RelPath(exePath);
                    bool isPrimary = string.Equals(IOPath.GetFileName(exePath),
                        primaryExeName, StringComparison.OrdinalIgnoreCase);
                    // Use ExeInfo (returns comment + registry uninstall path)
                    var (comment, regUninstall) = isPrimary
                        ? ExeInfo(exePath, appName, appVendor, appVersion)
                        : ExeInfo(exePath, "", "", "");

                    // Resolve silent install switch: explicit UI args (primary only) → /S fallback
                    string resolvedInstArgs = isPrimary && !string.IsNullOrWhiteSpace(installArgs)
                        ? installArgs
                        : "/S";
                    string argsPart = $" -ArgumentList '{resolvedInstArgs}'";

                    if (!string.IsNullOrEmpty(comment)) installSb.AppendLine(comment.Replace("    # ", "    # Install "));
                    installSb.AppendLine($"    Start-ADTProcess -FilePath \"$($adtSession.DirFiles)\\{exeRel}\"{argsPart} -WindowStyle \"Hidden\"");
                    installSb.AppendLine(); // blank line after command
                    log.AppendLine($"  EXE Install: {exeRel}");

                    // Auto-generate uninstall from registry UninstallString if found.
                    // Skip if an explicit Uninstall Source is already set — that block
                    // below (lines ~4810+) will add the command, avoiding duplicates.
                    if (!string.IsNullOrWhiteSpace(regUninstall) &&
                        string.IsNullOrEmpty(uninstallSource) &&
                        !regUninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
                    {
                        // Parse quoted path from UninstallString
                        string uPath = regUninstall.Trim();
                        string uArgs = "";
                        if (uPath.StartsWith("\""))
                        {
                            int end = uPath.IndexOf('"', 1);
                            if (end > 1) { uArgs = uPath[(end+1)..].Trim(); uPath = uPath[1..end]; }
                        }
                        else
                        {
                            int exeEnd = uPath.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                            if (exeEnd >= 0) { uArgs = uPath[(exeEnd+4)..].Trim(); uPath = uPath[..(exeEnd+4)]; }
                        }
                        // If the registry has no uninstall args, fall back to /S
                        if (string.IsNullOrWhiteSpace(uArgs)) uArgs = "/S";
                        string psPath    = ConvertToEnvSystemDrivePath(uPath);
                        string uArgsPart = $" -ArgumentList '{uArgs}'";
                        if (!string.IsNullOrEmpty(comment)) uninstallSb.AppendLine(comment.Replace("    # ", "    # Uninstall "));
                        AppendGuardedExeUninstall(uninstallSb, psPath, uArgsPart);
                        uninstallSb.AppendLine(); // blank line after command
                        log.AppendLine($"  EXE Uninstall (registry): {psPath}");
                    }
                }

                // ── MSI install + uninstall commands ──────────────────────
                foreach (string msiPath in allMsi)
                {
                    string msiRel  = RelPath(msiPath);
                    string comment = MsiComment(msiPath);
                    var msiProps = ReadMsiPropertiesSTA(msiPath);
                    msiProps.TryGetValue("ProductCode", out var msiPc);

                    if (!string.IsNullOrEmpty(comment)) installSb.AppendLine(comment.Replace("    # ", "    # Install "));
                    installSb.AppendLine($"    Start-ADTMsiProcess -Action 'Install' -FilePath \"$($adtSession.DirFiles)\\{msiRel}\"");
                    installSb.AppendLine(); // blank line after command

                    if (!string.IsNullOrWhiteSpace(msiPc))
                    {
                        if (!string.IsNullOrEmpty(comment)) uninstallSb.AppendLine(comment.Replace("    # ", "    # Uninstall "));
                        // Double quotes around the ProductCode - see the matching site above.
                        uninstallSb.AppendLine($"    Start-ADTMsiProcess -Action 'Uninstall' -ProductCode \"{msiPc}\"");
                        uninstallSb.AppendLine(); // blank line after command
                    }
                    log.AppendLine($"  MSI Install: {msiRel}");
                }
            }

            // ── MSP, BAT, ISS, REG, CER, INF commands (EXE flow) ─────────
            string[] CollectEF(string pattern) =>
                Directory.Exists(filesDir)
                    ? Directory.GetFiles(filesDir, pattern, SearchOption.AllDirectories)
                              .OrderBy(f => IOPath.GetDirectoryName(f)!.Length).ThenBy(f => f).ToArray()
                    : Array.Empty<string>();

            // MSP
            if (addMsp)
            {
                foreach (string mspPath in CollectEF("*.msp"))
                {
                    string mspRel = RelPath(mspPath);
                    string mspName = IOPath.GetFileName(mspPath);
                    installSb.AppendLine($"    # Install {mspName}");
                    installSb.AppendLine($"    Start-ADTMsiProcess -Action 'Patch' -FilePath \"$($adtSession.DirFiles)\\{mspRel}\"");
                    installSb.AppendLine();
                    log.AppendLine($"  MSP Install: {mspRel}");
                }
            }

            // ISS (InstallShield — paired EXE must have same base name)
            if (addIss)
            {
                foreach (string issPath in CollectEF("*.iss"))
                {
                    string issDir2 = IOPath.GetDirectoryName(issPath)!;
                    string issBase = IOPath.GetFileNameWithoutExtension(issPath);
                    string pairedExe = IOPath.Combine(issDir2, issBase + ".exe");
                    if (!File.Exists(pairedExe)) continue;
                    string issRel = RelPath(issPath);
                    string exeRel2 = RelPath(pairedExe);
                    var (issComment, _) = ExeInfo(pairedExe, appName, appVendor, appVersion);
                    if (!string.IsNullOrEmpty(issComment)) installSb.AppendLine(issComment);
                    installSb.AppendLine($"    Start-ADTProcess -FilePath \"$($adtSession.DirFiles)\\{exeRel2}\" -ArgumentList \"-s -f1`\"$($adtSession.DirFiles)\\{issRel}`\"\" -WindowStyle \"Hidden\"");
                    installSb.AppendLine();
                    log.AppendLine($"  ISS Install: {exeRel2} + {issRel}");
                }
            }

            // BAT
            if (addBat)
            {
                foreach (string batPath in CollectEF("*.bat"))
                {
                    string batRel = RelPath(batPath);
                    installSb.AppendLine($"    # Run {IOPath.GetFileName(batPath)}");
                    installSb.AppendLine($"    Start-ADTProcess -FilePath \"$($adtSession.DirFiles)\\{batRel}\" -WindowStyle \"Hidden\"");
                    installSb.AppendLine();
                    log.AppendLine($"  BAT Install: {batRel}");
                }
            }

            // REG
            if (addReg)
            {
                foreach (string regPath in CollectEF("*.reg"))
                {
                    string regRel = RelPath(regPath);
                    installSb.AppendLine($"    # Run {IOPath.GetFileName(regPath)}");
                    installSb.AppendLine($"    Start-ADTProcess -FilePath \"regedit.exe\" -ArgumentList \"/s `\"$($adtSession.DirFiles)\\{regRel}`\"\" -WindowStyle \"Hidden\"");
                    installSb.AppendLine();
                    log.AppendLine($"  REG Install: {regRel}");
                }
            }

            // CER
            if (addCer)
            {
                foreach (string cerPath in CollectEF("*.cer"))
                {
                    string cerRel  = RelPath(cerPath);
                    string cerName = IOPath.GetFileName(cerPath);
                    string certComment = $"    # Install {cerName}";
                    string certSerial  = "";
                    try
                    {
                        var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(cerPath);
                        string cn = cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
                        certSerial = cert.SerialNumber ?? "";
                        if (!string.IsNullOrWhiteSpace(cn)) certComment = $"    # Install {cn}";
                        using var store = new System.Security.Cryptography.X509Certificates.X509Store("TrustedPublisher",
                            System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
                        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                        var existing = store.Certificates.Find(
                            System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint, cert.Thumbprint, false);
                        if (existing.Count > 0 && !string.IsNullOrWhiteSpace(cn))
                            certComment = $"    # Install {cn}";
                    }
                    catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    installSb.AppendLine(certComment);
                    installSb.AppendLine($"    Start-ADTProcess -FilePath \"certutil.exe\" -ArgumentList \"-addstore TrustedPublisher `\"$($adtSession.DirFiles)\\{cerRel}`\"\" -WindowStyle \"Hidden\"");
                    installSb.AppendLine();
                    if (!string.IsNullOrWhiteSpace(certSerial))
                    {
                        uninstallSb.AppendLine(certComment.Replace("# Install", "# Uninstall"));
                        uninstallSb.AppendLine($"    Start-ADTProcess -FilePath \"certutil.exe\" -ArgumentList \"-delstore TrustedPublisher {certSerial}\" -WindowStyle \"Hidden\"");
                        uninstallSb.AppendLine();
                    }
                    log.AppendLine($"  CER: {cerRel}");
                }
            }

            // INF
            if (addInf)
            {
                foreach (string infPath in CollectEF("*.inf"))
                {
                    string infRel  = RelPath(infPath);
                    string infName = IOPath.GetFileName(infPath);
                    installSb.AppendLine($"    # Install {infName}");
                    installSb.AppendLine($"    Start-ADTProcess -FilePath \"pnputil.exe\" -ArgumentList \"/add-driver `\"$($adtSession.DirFiles)\\{infRel}`\" /install /force\" -WindowStyle \"Hidden\" -IgnoreExitCodes '512'");
                    installSb.AppendLine();
                    uninstallSb.AppendLine($"    # Uninstall {infName}");
                    uninstallSb.AppendLine($"    Start-ADTProcess -FilePath \"pnputil.exe\" -ArgumentList \"/delete-driver `\"$($adtSession.DirFiles)\\{infRel}`\" /uninstall /force\" -WindowStyle \"Hidden\"");
                    uninstallSb.AppendLine();
                    log.AppendLine($"  INF: {infRel}");
                }
            }

            // ── Uninstall: EXE uninstall command ─────────────────────────
            // Always add an uninstall command when an uninstall source is set,
            // whether it lives inside Files\ or is an external path (e.g. installed app).
            if (!string.IsNullOrEmpty(uninstallSource))
            {
                string uninstExeName = IOPath.GetFileName(uninstallSource);
                // Resolve uninstall args: explicit UI args → registry lookup via ExeInfo →
                // /S fallback
                string resolvedUninstArgs = uninstallArgs;
                if (string.IsNullOrWhiteSpace(resolvedUninstArgs)) resolvedUninstArgs = "/S";
                string uArgsPart = $" -ArgumentList '{resolvedUninstArgs}'";
                var (uComment, _) = ExeInfo(uninstallSource, appName, appVendor, appVersion);

                bool inFiles = Directory.Exists(filesDir) &&
                    (uninstallSource.StartsWith(filesDir, StringComparison.OrdinalIgnoreCase)
                     || Directory.GetFiles(filesDir, uninstExeName, SearchOption.AllDirectories).Any());

                if (inFiles)
                {
                    // EXE is inside Files\ — use DirFiles relative path
                    string? uninstFull = uninstallSource.StartsWith(filesDir, StringComparison.OrdinalIgnoreCase)
                        ? uninstallSource
                        : Directory.GetFiles(filesDir, uninstExeName, SearchOption.AllDirectories).FirstOrDefault();
                    string uninstRel = uninstFull != null
                        ? uninstFull.Substring(filesDir.Length).TrimStart(IOPath.DirectorySeparatorChar,
                                                                           IOPath.AltDirectorySeparatorChar)
                        : uninstExeName;
                    if (!string.IsNullOrEmpty(uComment)) uninstallSb.AppendLine(uComment.Replace("    # ", "    # Uninstall "));
                    uninstallSb.AppendLine($"    Start-ADTProcess -FilePath \"$($adtSession.DirFiles)\\{uninstRel}\"{uArgsPart} -WindowStyle \"Hidden\"");
                    uninstallSb.AppendLine(); // blank line after command
                    log.AppendLine($"  EXE Uninstall (from Files): {uninstRel}");
                }
                else
                {
                    // External path (e.g. installed app uninstaller)
                    string psPath = ConvertToEnvSystemDrivePath(uninstallSource);
                    if (!string.IsNullOrEmpty(uComment)) uninstallSb.AppendLine(uComment.Replace("    # ", "    # Uninstall "));
                    AppendGuardedExeUninstall(uninstallSb, psPath, uArgsPart);
                    uninstallSb.AppendLine(); // blank line after command
                    log.AppendLine($"  EXE Uninstall (external): {psPath}");
                }
            }

            string installBlock   = installSb.ToString().TrimEnd();
            string uninstallBlock = uninstallSb.ToString().TrimEnd();

            if (!string.IsNullOrEmpty(installBlock))
                content = InjectAfterComment(content,
                    "## <Perform Installation tasks here>", installBlock, log);
            if (!string.IsNullOrEmpty(uninstallBlock))
                content = InjectAfterComment(content,
                    "## <Perform Uninstallation tasks here>", uninstallBlock, log);

            // ── ARP Flags (NoModify / NoRepair / NoRemove) ────────────────
            // Inject Set-ADTRegistryKey lines after ## <Perform Post-Installation tasks here>
            // only when the JSON config explicitly sets each flag to "Yes" / "true".
            bool doNoModifyE = cfg?.AddNoModify ?? false;
            bool doNoRepairE = cfg?.AddNoRepair ?? false;
            bool doNoRemoveE = cfg?.AddNoRemove ?? false;

            if (doNoModifyE || doNoRepairE || doNoRemoveE)
            {
                const string POST_MARKER_E = "## <Perform Post-Installation tasks here>";

                string arpFallbackLabelE = string.Join(" ", new[] { appVendor, appName, appVersion }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

                var uninstallRegKeyMapE = new System.Collections.Generic.Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

                static string NormaliseE(string path) => path
                    .Replace("HKLM:\\", "HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
                    .Replace("HKCU:\\", "HKEY_CURRENT_USER\\",  StringComparison.OrdinalIgnoreCase)
                    .Replace("HKLM\\",  "HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
                    .Replace("HKCU\\",  "HKEY_CURRENT_USER\\",  StringComparison.OrdinalIgnoreCase);

                // Use the primary EXE (installExeFullPath) plus any EXEs in Files\
                string filesDirE = IOPath.Combine(IOPath.GetDirectoryName(scriptPath)!, "Files");
                var exesToScan = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(installExeFullPath) && File.Exists(installExeFullPath))
                    exesToScan.Add(installExeFullPath);
                if (Directory.Exists(filesDirE))
                    exesToScan.AddRange(Directory.GetFiles(filesDirE, "*.exe", SearchOption.AllDirectories));

                foreach (string ef in exesToScan)
                {
                    string nameHintE = !string.IsNullOrWhiteSpace(appName) ? appName : "";
                    if (string.IsNullOrWhiteSpace(nameHintE))
                    {
                        try { var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(ef); nameHintE = vi.ProductName?.Trim() ?? ""; } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    }
                    if (string.IsNullOrWhiteSpace(nameHintE)) nameHintE = IOPath.GetFileNameWithoutExtension(ef);
                    var regE2 = LookupInstalledByExe(ef, nameHintE);
                    if (regE2 != null && !string.IsNullOrWhiteSpace(regE2.RegKeyPath))
                    {
                        string litPath = NormaliseE(regE2.RegKeyPath);
                        string label   = string.Join(" ", new[] { regE2.Publisher, regE2.DisplayName, regE2.DisplayVersion }
                            .Where(s => !string.IsNullOrWhiteSpace(s)));
                        if (!uninstallRegKeyMapE.ContainsKey(litPath))
                            uninstallRegKeyMapE[litPath] = label;
                    }
                }
                // Scan MSIs in Files\
                if (Directory.Exists(filesDirE))
                {
                    foreach (string mf in Directory.GetFiles(filesDirE, "*.msi", SearchOption.AllDirectories))
                    {
                        var msiPropsE = ReadMsiPropertiesSTA(mf);
                        msiPropsE.TryGetValue("ProductCode", out var msiPcE);
                        msiPropsE.TryGetValue("ProductName", out var msiPnE);
                        ExeInstalledInfo? regME = !string.IsNullOrWhiteSpace(msiPcE)
                            ? LookupInstalledByProductCode(msiPcE) : null;
                        if (regME == null && !string.IsNullOrWhiteSpace(msiPnE))
                            regME = LookupInstalledByExe("", msiPnE);
                        if (regME != null && !string.IsNullOrWhiteSpace(regME.RegKeyPath))
                        {
                            string litPath = NormaliseE(regME.RegKeyPath);
                            string label   = string.Join(" ", new[] { regME.Publisher, regME.DisplayName, regME.DisplayVersion }
                                .Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!uninstallRegKeyMapE.ContainsKey(litPath))
                                uninstallRegKeyMapE[litPath] = label;
                        }
                    }
                }

                // Fallback: scan all three uninstall hives directly by appName
                if (uninstallRegKeyMapE.Count == 0 && !string.IsNullOrWhiteSpace(appName))
                {
                    log.AppendLine($"  ARP (EXE): filesDir scan found nothing - scanning registry hives by AppName '{appName}'…");
                    var hiveRootsE = new[]
                    {
                        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",           Microsoft.Win32.Registry.LocalMachine),
                        (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", Microsoft.Win32.Registry.LocalMachine),
                        (@"Software\Microsoft\Windows\CurrentVersion\Uninstall",           Microsoft.Win32.Registry.CurrentUser),
                    };
                    foreach (var (subPathE, hiveE) in hiveRootsE)
                    {
                        try
                        {
                            using var parentE = hiveE.OpenSubKey(subPathE);
                            if (parentE == null) continue;
                            foreach (string subE in parentE.GetSubKeyNames())
                            {
                                try
                                {
                                    using var kE = parentE.OpenSubKey(subE);
                                    if (kE == null) continue;
                                    string dnE = kE.GetValue("DisplayName") as string ?? "";
                                    if (string.IsNullOrWhiteSpace(dnE)) continue;
                                    if (!dnE.Contains(appName, StringComparison.OrdinalIgnoreCase) &&
                                        !appName.Contains(dnE,  StringComparison.OrdinalIgnoreCase)) continue;
                                    string pubE  = kE.GetValue("Publisher")      as string ?? "";
                                    string verE  = kE.GetValue("DisplayVersion") as string ?? "";
                                    string hiveNameE = hiveE == Microsoft.Win32.Registry.LocalMachine
                                        ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
                                    string litPathE = $"{hiveNameE}\\{subPathE}\\{subE}";
                                    string labelE   = string.Join(" ", new[] { pubE, dnE, verE }
                                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                                    if (!uninstallRegKeyMapE.ContainsKey(litPathE))
                                    {
                                        uninstallRegKeyMapE[litPathE] = labelE;
                                        log.AppendLine($"  ARP (EXE): found via hive scan → {litPathE}");
                                    }
                                }
                                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                            }
                        }
                        catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    }
                    if (uninstallRegKeyMapE.Count == 0)
                        log.AppendLine($"  ARP (EXE): app '{appName}' not found in any uninstall hive on this machine - skipped.");
                }

                if (uninstallRegKeyMapE.Count > 0)
                {
                    var arpLinesE = new System.Collections.Generic.List<string>();

                    foreach (var kvp in uninstallRegKeyMapE)
                    {
                        string litPath = kvp.Key;

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
                        catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                        var appArpLinesE = new System.Collections.Generic.List<string>();

                        if (doNoModifyE)
                        {
                            bool alreadySet = liveKey?.GetValue("NoModify") != null;
                            if (!alreadySet)
                                appArpLinesE.Add($"    Set-ADTRegistryKey -LiteralPath \"{litPath}\" -Name \"NoModify\" -Value \"1\" -Type \"DWord\"");
                            else
                                log.AppendLine($"  NoModify already set in registry for {litPath} - skipped");
                        }
                        if (doNoRepairE)
                        {
                            bool alreadySet = liveKey?.GetValue("NoRepair") != null;
                            if (!alreadySet)
                                appArpLinesE.Add($"    Set-ADTRegistryKey -LiteralPath \"{litPath}\" -Name \"NoRepair\" -Value \"1\" -Type \"DWord\"");
                            else
                                log.AppendLine($"  NoRepair already set in registry for {litPath} - skipped");
                        }
                        if (doNoRemoveE)
                        {
                            bool alreadySet = liveKey?.GetValue("NoRemove") != null;
                            if (!alreadySet)
                                appArpLinesE.Add($"    Set-ADTRegistryKey -LiteralPath \"{litPath}\" -Name \"NoRemove\" -Value \"1\" -Type \"DWord\"");
                            else
                                log.AppendLine($"  NoRemove already set in registry for {litPath} - skipped");
                        }

                        liveKey?.Dispose();

                        if (appArpLinesE.Count > 0)
                        {
                            string perAppLabel = kvp.Value;
                            if (string.IsNullOrWhiteSpace(perAppLabel)) perAppLabel = arpFallbackLabelE;
                            string perAppComment = string.IsNullOrWhiteSpace(perAppLabel)
                                ? "# ARP flags - prevent Modify/Repair/Remove from Add/Remove Programs"
                                : $"# ARP flags - {perAppLabel}";
                            arpLinesE.Add("    " + perAppComment);
                            arpLinesE.AddRange(appArpLinesE);
                            arpLinesE.Add("");
                        }
                    }

                    while (arpLinesE.Count > 0 && arpLinesE[arpLinesE.Count - 1] == "")
                        arpLinesE.RemoveAt(arpLinesE.Count - 1);
                    arpLinesE.Add("");

                    if (arpLinesE.Count > 1)
                    {
                        var filteredArpLinesE = new System.Collections.Generic.List<string>();
                        var blockCommentE  = new System.Collections.Generic.List<string>();
                        var blockSetLinesE = new System.Collections.Generic.List<string>();

                        void FlushBlockE()
                        {
                            if (blockSetLinesE.Count == 0) { blockCommentE.Clear(); return; }
                            bool allPresent = blockSetLinesE.All(l =>
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(
                                    l, @"-LiteralPath\s+""([^""]+)""",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                return m.Success && content.Contains(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
                            });
                            if (!allPresent)
                            {
                                filteredArpLinesE.AddRange(blockCommentE);
                                filteredArpLinesE.AddRange(blockSetLinesE);
                                filteredArpLinesE.Add("");
                            }
                            blockCommentE.Clear();
                            blockSetLinesE.Clear();
                        }

                        foreach (var line in arpLinesE)
                        {
                            if (line.TrimStart().StartsWith("# ARP flags", StringComparison.OrdinalIgnoreCase))
                            {
                                FlushBlockE();
                                blockCommentE.Add(line);
                            }
                            else if (line.TrimStart().StartsWith("Set-ADTRegistryKey", StringComparison.OrdinalIgnoreCase))
                                blockSetLinesE.Add(line);
                        }
                        FlushBlockE();

                        while (filteredArpLinesE.Count > 0 && filteredArpLinesE[filteredArpLinesE.Count - 1] == "")
                            filteredArpLinesE.RemoveAt(filteredArpLinesE.Count - 1);

                        if (filteredArpLinesE.Count > 0)
                        {
                            filteredArpLinesE.Add("");
                            content = InjectAfterComment(content, POST_MARKER_E,
                                string.Join(Environment.NewLine, filteredArpLinesE), log);
                            log.AppendLine("  Injected ARP flag command(s) after Post-Installation marker");
                        }
                        else
                            log.AppendLine("  ARP flags already present in script for all apps - skipped");
                    }
                }
                else
                    log.AppendLine("  ARP flags: no matching uninstall registry entry found - skipped");
            }

            File.WriteAllText(scriptPath, content, new UTF8Encoding(true));
        }

        // ── Convert absolute path to use $env:SystemDrive (PS script) ────
        // Only replaces C:\ with $env:SystemDrive — D:\ and UNC paths kept as-is
        /// <summary>
        /// Writes an EXE uninstall command guarded by Test-Path.
        ///
        /// An uninstaller reached by its path on disk is the one thing in the Uninstall block that
        /// may simply not be there: a different version installed to a different folder, a user who
        /// already removed the app, a machine where the install never landed. Start-ADTProcess
        /// against a missing FilePath throws, and the whole uninstall deployment fails - on a
        /// machine where the app is already gone, which is the state the uninstall wanted anyway.
        ///
        /// The path is passed once and written twice from the same string, so the guard can never
        /// end up testing a different location than the command runs.
        ///
        /// Only for paths. An uninstaller shipped inside Files\ is guaranteed present by the
        /// package itself, and an MSI uninstall goes by ProductCode through Start-ADTMsiProcess,
        /// where there is no file for Test-Path to look at.
        /// </summary>
        private static void AppendGuardedExeUninstall(StringBuilder sb, string psPath, string argsPart)
        {
            sb.AppendLine($"    If(Test-Path \"{psPath}\"){{");
            sb.AppendLine($"    Start-ADTProcess -FilePath \"{psPath}\"{argsPart} -WindowStyle \"Hidden\"");
            sb.AppendLine("    }");
        }

        private static string ConvertToEnvSystemDrivePath(string fullPath)
        {
            if (fullPath.Length >= 3 && fullPath[1] == ':')
            {
                char drive = char.ToUpper(fullPath[0]);
                if (drive == 'C')
                    return "$env:SystemDrive" + fullPath.Substring(2);
                // Non-C drive — keep full literal path
                return fullPath;
            }
            // UNC path (\\server\share) or relative — keep as-is
            return fullPath;
        }

        // ── Convert absolute path for display in command textbox ──────────
        // C:\abc\app.exe  → "%SystemDrive%\abc\app.exe"
        // D:\abc\app.exe  → "D:\abc\app.exe"   (literal)
        // \\abc\app.exe   → "\\abc\app.exe"     (literal)
        private static string ConvertToCmdSystemDrivePath(string fullPath)
        {
            if (fullPath.Length >= 3 && fullPath[1] == ':')
            {
                char drive = char.ToUpper(fullPath[0]);
                if (drive == 'C')
                    return "\"%SystemDrive%" + fullPath.Substring(2) + "\"";
                // Non-C drive — quote the full literal path
                return $"\"{fullPath}\"";
            }
            // UNC — quote as-is
            return $"\"{fullPath}\"";
        }
        private async Task BtnCreate_Exe()
        {
            // Validate
            string src = ExeTxtSource?.Text.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(src) || (!File.Exists(src) && !Directory.Exists(src)))
            {
                ShowValidationDialog(
                    "Auto Package - Validation",
                    string.IsNullOrWhiteSpace(src)
                        ? "Source file or folder is required. Please browse or drag-and-drop an installer file or folder."
                        : $"Source path not found:\n{src}\n\nPlease browse to a valid file or folder.",
                    "⚠️");
                ExeTxtSource?.Focus();
                return;
            }
            if (ExeTxtSourceError != null) ExeTxtSourceError.Visibility = Visibility.Collapsed;

            string packageName = ExeTxtPackageName?.Text.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(packageName))
            {
                ShowValidationDialog(
                    "Auto Package - Validation",
                    "Package Name is empty. Please fill in the application info.",
                    "⚠️");
                return;
            }

            // ── Folder-exists: auto-increment revision if triggered from Source Download, else show dialog ──
            {
                string checkRoot = _cfg.ResolvedPackageOutputFolder;
                if (_triggeredFromSourceDownload)
                {
                    // Auto-increment App Revision until a free folder name is found
                    packageName = AutoIncrementRevisionUntilFree(checkRoot, isExeTab: true);
                }
                else
                {
                    string checkPath = IOPath.Combine(checkRoot, packageName);
                    if (Directory.Exists(checkPath))
                    {
                        bool overwrite = ShowFolderExistsDialog(checkPath);
                        if (!overwrite) return;
                    }
                }
                // Always reset the flag after consuming it
                _triggeredFromSourceDownload = false;
            }

            _busy = true;
            _createStartTime = DateTime.Now;
            _createCts = new System.Threading.CancellationTokenSource();
            _createPause.Set();
            _creationPaused = false;
            var token = _createCts.Token;
            SetBusy(true);

            // Snapshot all UI values on UI thread
            string rootBase       = _cfg.ResolvedPackageOutputFolder;
            // Re-read packageName after possible revision increment
            packageName = ExeTxtPackageName?.Text.Trim() ?? packageName;
            string path1          = IOPath.Combine(rootBase, packageName);    // C:\Packages\VendorName_AppName_1.0_01
            string path2          = path1;                                     // template copied directly into path1
            string filesPath      = IOPath.Combine(path1, "Files");           // installer source → path1\Files
            string intunePath     = IOPath.Combine(path1, "Intune");
            _packageRootPath      = path1;

            string ts       = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _logPath         = IOPath.Combine(IOPath.GetTempPath(), $"AutoPackage_{packageName}.log");
            // Start a fresh per-package log for this run.
            try { File.WriteAllText(_logPath, string.Empty); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            var log          = new StringBuilder();
            try { if (PkgEventLog != null) { PkgEventLog.Clear(); } } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            void Log(string msg) {
                string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
                log.AppendLine(line);
                AppendPkgEventLog(line);
            }

            // FIX: Derive exeInstallPath from the current src (ExeTxtSource.Text) rather
            // than the cached _exePath field.  _exePath can be stale — e.g. it may still
            // hold a path like C:\Temp\down\FileCompare.exe from a previous AllDirectories scan
            // even when the user has since selected the parent folder C:\Temp.  Using src
            // as the authoritative source and only looking in TopDirectoryOnly ensures we
            // never accidentally copy a single buried EXE instead of the whole folder.
            string exeInstallPath;
            if (File.Exists(src))
                exeInstallPath = src;  // user picked a specific file
            else if (Directory.Exists(src))
            {
                // Folder source: find the primary installer for the .intunewin entry point.
                // The full folder is always copied by CopyDirectory in Step 3 — this path
                // is only used to determine which file to use as the intunewin entry point
                // and for registry lookup / field population.
                // Priority: root first (most likely), then subdirs (e.g. C:\Temp\down\FileCompare.exe)
                exeInstallPath =
                    Directory.GetFiles(src, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault()
                 ?? Directory.GetFiles(src, "*.msi", SearchOption.TopDirectoryOnly).FirstOrDefault()
                 ?? Directory.GetFiles(src, "*.exe", SearchOption.AllDirectories).FirstOrDefault()
                 ?? Directory.GetFiles(src, "*.msi", SearchOption.AllDirectories).FirstOrDefault()
                 ?? Directory.GetFiles(src, "*.ps1", SearchOption.TopDirectoryOnly).FirstOrDefault()
                 ?? Directory.GetFiles(src, "*.ps1", SearchOption.AllDirectories).FirstOrDefault()
                 ?? _exePath;
            }
            else
                exeInstallPath = _exePath;  // fallback for edge cases
            // Read uninstall path from UI textbox — covers browse, drop, registry-auto-fill, and typed input.
            // Do NOT fall back to _exePath: using the install EXE as the uninstaller is wrong.
            // If no explicit uninstall source is set, leave it empty — PatchInvokeScriptForExe
            // will auto-generate the uninstall command from the registry UninstallString.
            string uiUninstallSrc    = ExeTxtUninstallSource?.Text.Trim() ?? "";
            string exeUninstallPath  = !string.IsNullOrEmpty(uiUninstallSrc)  ? uiUninstallSrc
                                     : !string.IsNullOrEmpty(_exeUninstallPath) ? _exeUninstallPath
                                     : "";  // empty → let registry lookup in PatchInvokeScriptForExe provide it
            string installArgs       = ExeTxtInstallArgs?.Text.Trim()   ?? "";
            string uninstallArgs     = ExeTxtUninstallArgs?.Text.Trim() ?? "";
            string templateFolder    = ExeTxtTemplateFolder?.Text.Trim() ?? "";
            AutoPackageAppEntry? perApp = _staticSelectedApp;
            // Controls removed from Package tab — per-app JSON overrides global config
            string exePerAppCreateIntunewin = perApp?.CreateIntunewin?.Trim() ?? "";
            bool   doIntunewin       = !string.IsNullOrWhiteSpace(exePerAppCreateIntunewin)
                                       ? exePerAppCreateIntunewin.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                                       : (_cfg?.CreateIntunewin ?? false);
            string exePerAppPublishToIntune = perApp?.PublishToIntune?.Trim() ?? "";
            string exePerAppPublishSettings = perApp?.PublishSettings?.Trim() ?? "";
            bool   doPublish         = (!string.IsNullOrWhiteSpace(exePerAppPublishToIntune)
                                       ? exePerAppPublishToIntune.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                                       : (_cfg?.PublishToIntune ?? false))
                                    || (!string.IsNullOrWhiteSpace(exePerAppPublishSettings)
                                       ? exePerAppPublishSettings.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                                       : false);
            // Read InstallPackage from per-app JSON first, fall back to global config
            string exePerAppInstallPkg = perApp?.InstallPackage?.Trim() ?? "";
            bool   doInstallPkg      = (!string.IsNullOrWhiteSpace(exePerAppInstallPkg) && !exePerAppInstallPkg.Equals("No", StringComparison.OrdinalIgnoreCase))
                                    || (!string.IsNullOrWhiteSpace(_cfg?.InstallPackage) && _cfg.InstallPackage != "No");
            string rawInstallVal       = doInstallPkg
                ? (exePerAppInstallPkg.Equals("System Context", StringComparison.OrdinalIgnoreCase) ? "System Context" : "Administrator")
                : "No";
            string exeCapDesc          = perApp?.Description ?? _cfg?.IntuneSettings?.Description ?? "";
            string description         = exeCapDesc;
            string deploymentType      = "";
            // Read UninstallPackage from per-app JSON first, fall back to global config
            string exePerAppUninstallPkg = perApp?.UninstallPackage?.Trim() ?? "";
            bool   doUninstallPkg    = (!string.IsNullOrWhiteSpace(exePerAppUninstallPkg) && !exePerAppUninstallPkg.Equals("No", StringComparison.OrdinalIgnoreCase))
                                    || (!string.IsNullOrWhiteSpace(_cfg?.UninstallPackage) && _cfg.UninstallPackage != "No");
            string rawUninstallVal2    = doUninstallPkg
                ? (exePerAppUninstallPkg.Equals("System Context", StringComparison.OrdinalIgnoreCase) ? "System Context" : "Administrator")
                : "No";
            bool   doCreateDoc       = FindName("ExeCmbCreateDocument") is ComboBox exeCmbCD && ComboIsYes(exeCmbCD);

            // ── Always register post-create callback — overwrites MainWindow.SetAfterCreateCallback ──
            {
                bool captureInstall      = doInstallPkg;
                bool captureUninstall    = doUninstallPkg;
                // Guard: if the boolean flag is true but the combo value is still "No" (e.g. when
                // triggered from SourceDownload checkboxes which pass doInstallPkg=true but
                // _staticPendingInstallPackage="No"), fall back to "Administrator" so
                // NavigateToExecuteTab sees a non-"No" value and waits for Execute to complete.
                string captureInstallVal   = (captureInstall   && rawInstallVal.Equals("No",   StringComparison.OrdinalIgnoreCase))
                                             ? "Administrator" : rawInstallVal;
                string captureUninstallVal = (captureUninstall && rawUninstallVal2.Equals("No", StringComparison.OrdinalIgnoreCase))
                                             ? "Administrator" : rawUninstallVal2;
                _onAfterCreateComplete = () =>
                {
                    Action? afterExec = _pendingAfterExecuteChain;
                    _pendingAfterExecuteChain = null;

                    // ── Sequencing rule for CreateIntunewin=Yes (EXE tab) ────────────────
                    // Same as MSI tab: Install → Uninstall → Intunewin, all in one
                    // NavigateToExecuteTab call. afterExec (= intunewinThenPublish) fires
                    // only after Uninstall completes. No deferred-to-after-Publish path.
                    // ─────────────────────────────────────────────────────────────────────

                    if (captureInstall || captureUninstall)
                    {
                        string? ps1 = Directory.GetFiles(LastCreatedPackageFolder,
                            "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories).FirstOrDefault();

                        // When Publish (but NOT Intunewin) is active and uninstall runs,
                        // wire onAfterExecute to navigate to Publish tab after uninstall.
                        Action? publishNavActionExe = null;
                        if (_triggerPublishTab && !doIntunewin && captureUninstall)
                        {
                            string _cpf2   = LastCreatedPackageFolder;
                            string _cpn2   = _publishCapAppName;
                            string _cpv2   = _publishCapPublisher;
                            string _cpver2 = _publishCapVersion;
                            string _cpd2   = _publishCapDesc;
                            publishNavActionExe = () => Dispatcher.Invoke(() =>
                                NavigateToPublishTab(_cpf2, appName: _cpn2, appVendor: _cpv2,
                                    appVersion: _cpver2, description: _cpd2));
                        }

                        // When nothing downstream is configured (no Intunewin, no Publish) the
                        // executed step (Install/Uninstall package) is the terminus, so fall back to
                        // completing the flow here - otherwise onAfterExecute is null, the auto-flow
                        // lock is never released, and the six sub-tabs stay frozen on the Execute tab
                        // after a successful Uninstall package on the EXE path. Mirrors BtnCreate_Click.
                        Action? effectiveAfterExecExe = publishNavActionExe ?? afterExec
                            ?? (() => Dispatcher.Invoke(() => RaiseAutoFlowComplete()));

                        if (!string.IsNullOrWhiteSpace(ps1))
                        {
                            // Pre-fill Publish tab immediately after Install (app still installed)
                            // so registry-based detection fields are captured before Uninstall runs.
                            // When an Install runs, capture the app's uninstall-registry details
                            // right after it completes (while the app is still installed) so the
                            // Document tab's Display Name / Publisher / Version / Install Location
                            // survive the later Uninstall. The Publish-tab prefill additionally
                            // requires Uninstall + Intunewin/Publish to be part of the flow.
                            Action? preFillPublishExe = captureInstall
                                ? () => Dispatcher.Invoke(() =>
                                  {
                                      CaptureUninstallRegistryForDocument();
                                      if ((doIntunewin || _triggerPublishTab) && captureUninstall)
                                      {
                                          NavigateToPublishTab(LastCreatedPackageFolder,
                                              appName:    _publishCapAppName,
                                              appVendor:  _publishCapPublisher,
                                              appVersion: _publishCapVersion,
                                              description: _publishCapDesc,
                                              prefillOnly: true);
                                          // Stay on Execute tab so the user sees uninstall progress
                                          ActivateTopTab("Execute");
                                      }
                                  })
                                : null;

                            NavigateToExecuteTab(ps1,
                                installPackage:   captureInstall   ? captureInstallVal   : "No",
                                uninstallPackage: captureUninstall ? captureUninstallVal : "No",
                                onAfterExecute:   effectiveAfterExecExe,
                                onAfterInstall:   preFillPublishExe);
                        }
                        else
                        {
                            // MainWindow.NavigateToExecutePackage* — reflection to avoid compile-time dependency
                            _pendingAfterExecuteChain = effectiveAfterExecExe;
                            var win = Window.GetWindow(this);
                            if (win != null)
                            {
                                string mwMethod = (captureInstall && captureUninstall)
                                    ? "NavigateToExecutePackageAndInstallThenUninstall"
                                    : captureInstall
                                        ? "NavigateToExecutePackageAndInstall"
                                        : "NavigateToExecutePackageAndUninstall";
                                try { win.GetType().GetMethod(mwMethod)?.Invoke(win, new object[] { LastCreatedPackageFolder }); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                            }
                        }
                    }
                    else
                    {
                        // No Execute step — fire Intunewin→Publish chain directly.
                        // Do NOT reset GlobalShowMessageBox here — reset happens in IwBtnCreate_Click.
                        if (afterExec != null)
                            afterExec.Invoke();
                        else
                            // Nothing downstream is configured (no Install/Uninstall package,
                            // no Intunewin, no Publish) — package creation is the terminus,
                            // so signal completion here to unlock the Download tab.
                            RaiseAutoFlowComplete();
                    }
                };
            }

            // ── Intunewin / Publish tab signals (independent of _onAfterCreateComplete) ──
            bool   exeTriggerIntunewin = doIntunewin;
            bool   exeTriggerPublish   = doPublish;
            string exeCapAppName    = ExeTxtAppName?.Text.Trim()    ?? "";
            string exeCapPublisher  = ExeTxtPublisher?.Text.Trim()  ?? "";
            string exeCapAppVersion = ExeTxtAppVersion?.Text.Trim() ?? "";
            _triggerIntunewinTab = exeTriggerIntunewin;
            _triggerPublishTab   = exeTriggerPublish;
            _publishCapAppName   = exeCapAppName;
            _publishCapPublisher = exeCapPublisher;
            _publishCapVersion   = exeCapAppVersion;
            _publishCapDesc      = exeCapDesc;

            // When the Intunewin tab will handle creation after the success dialog,
            // skip the EXE-flow background intunewin step to avoid a silent duplicate run.
            string appName           = ExeTxtAppName?.Text.Trim()    ?? "";
            string appVendor         = ExeTxtPublisher?.Text.Trim()  ?? "";
            string appVersion        = ExeTxtAppVersion?.Text.Trim() ?? "";
            string appRevision       = ExeTxtAppRevision?.Text.Trim() ?? "01";
            string installCmd        = ExeTxtInstallCmd?.Text.Trim()   ?? "";
            string uninstallCmd      = ExeTxtUninstallCmd?.Text.Trim() ?? "";
            string exeCloseApps      = ExeTxtCloseApps?.Text.Trim()    ?? "";

            try
            {
                bool exeFlowCancelled = false;
                await Task.Run(() =>
                {
                    Log($"=== SwiftPack Auto Package (EXE) - {packageName} ===");
                    Log($"EXE src (ExeTxtSource)  : {src}");
                    Log($"EXE exeInstallPath      : {exeInstallPath}");
                    Log($"EXE _exePath field      : {_exePath}");
                    Log($"Path1                   : {path1}");

                    // Local helper: waits at pause gate, returns true if cancelled.
                    // Never throws — all cancellation via exeFlowCancelled flag.
                    bool Paused()
                    {
                        while (_createPause.IsSet == false)
                        {
                            if (token.IsCancellationRequested) { exeFlowCancelled = true; return true; }
                            System.Threading.Thread.Sleep(100);
                        }
                        if (token.IsCancellationRequested) { exeFlowCancelled = true; return true; }
                        return false;
                    }

                    // Step 1: Create package root folder
                    Log("Step 1/8 - Creating package folder…");
                    bool hasTemplate = !string.IsNullOrWhiteSpace(templateFolder) && Directory.Exists(templateFolder);
                    Directory.CreateDirectory(path1);
                    SetProgress(12);
                    if (Paused()) return;

                    // Step 2: Copy template
                    // Position the copied template so the folder containing
                    // Invoke-AppDeployToolkit.ps1 sits ONE level below the package root:
                    //   • ps1 at template root        → wrap into <path1>\Package
                    //   • ps1 in a template subfolder → copy as-is (e.g. AppPackage, Output\AppPackage)
                    // Intune / Documents are then created beside that folder (its parent).
                    Log("Step 2/8 - Copying template…");
                    string templateCopyDest = path1;
                    if (hasTemplate)
                    {
                        string? tplPs1 = FindDeploymentPs1(templateFolder);
                        string tplPs1Dir = tplPs1 != null ? (IOPath.GetDirectoryName(tplPs1) ?? templateFolder) : templateFolder;
                        bool ps1AtTemplateRoot = tplPs1 != null && SamePath(tplPs1Dir, templateFolder);
                        if (ps1AtTemplateRoot)
                            templateCopyDest = IOPath.Combine(path1, "Package");
                        Directory.CreateDirectory(templateCopyDest);
                        CopyDirectory(templateFolder, templateCopyDest);
                        Log($"  Template copied: {templateFolder} → {templateCopyDest}");
                    }
                    else Log("  No template folder - skipped.");

                    // Resolve the output base (parent of the ps1's folder) and place the
                    // Intune folder there so it is a sibling of the app-package folder.
                    string outputBase = ResolveOutputBase(path1);
                    intunePath = IOPath.Combine(outputBase, "Intune");
                    if (doIntunewin) Directory.CreateDirectory(intunePath);
                    Log($"  Output base   : {outputBase}");
                    Log($"  Intune folder : {intunePath}");
                    SetProgress(25);
                    if (Paused()) return;

                    // Step 2b: Find Files\ as sibling of Invoke-AppDeployToolkit.ps1, wipe its contents
                    string ResolveFilesFolder2()
                    {
                        string? ps1 = Directory.GetFiles(path1, "Invoke-AppDeployToolkit.ps1",
                                          SearchOption.AllDirectories).FirstOrDefault();
                        if (ps1 != null)
                        {
                            string sibling = IOPath.Combine(IOPath.GetDirectoryName(ps1) ?? path1, "Files");
                            if (!Directory.Exists(sibling)) Directory.CreateDirectory(sibling);
                            return sibling;
                        }
                        string fallback = IOPath.Combine(path1, "Files");
                        Directory.CreateDirectory(fallback);
                        return fallback;
                    }
                    string resolvedFilesFolder = hasTemplate ? ResolveFilesFolder2() : IOPath.Combine(path1, "Files");
                    filesPath = resolvedFilesFolder;
                    if (hasTemplate && Directory.Exists(filesPath))
                    {
                        Log("  Clearing Files\\ contents from template…");
                        foreach (string ff in Directory.GetFiles(filesPath, "*", SearchOption.AllDirectories))
                            try { File.Delete(ff); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        foreach (string dd in Directory.GetDirectories(filesPath).OrderByDescending(x => x.Length))
                            try { Directory.Delete(dd, recursive: true); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        Log("  Files\\ cleared.");
                    }

                    // Step 3: Copy source into Files\
                    string destDir = hasTemplate ? filesPath : path1;
                    Log($"Step 3/8 - Copying source → {destDir}");
                    Directory.CreateDirectory(destDir);
                    if (Directory.Exists(src))
                    {
                        CopyDirectory(src, destDir);
                        Log($"  Copied folder contents: {src}");
                    }
                    else if (File.Exists(exeInstallPath))
                    {
                        string destExe = IOPath.Combine(destDir, IOPath.GetFileName(exeInstallPath));
                        File.Copy(exeInstallPath, destExe, true);
                        Log($"  Copied: {IOPath.GetFileName(exeInstallPath)}");
                    }
                    SetProgress(40);
                    if (Paused()) return;

                    // Step 4: Patch Invoke-AppDeployToolkit.ps1
                    // Copy from PackageTemplateFolder ONLY if TemplateFolder was set in the app JSON.
                    bool hasTemplateFolderExe = Dispatcher.Invoke(() => !string.IsNullOrWhiteSpace(ExeTxtTemplateFolder?.Text?.Trim()));
                    if (_appTemplateFolderEnabled && !string.IsNullOrWhiteSpace(_pendingTemplateFolder))
                    {
                        string srcPs1 = IOPath.Combine(_pendingTemplateFolder, "Invoke-AppDeployToolkit.ps1");
                        if (File.Exists(srcPs1))
                        {
                            string? existingPs1 = Directory.GetFiles(path2, "Invoke-AppDeployToolkit.ps1",
                                                     SearchOption.AllDirectories).FirstOrDefault();
                            string destPs1 = existingPs1 ?? IOPath.Combine(path2, "Invoke-AppDeployToolkit.ps1");
                            Directory.CreateDirectory(IOPath.GetDirectoryName(destPs1)!);
                            File.Copy(srcPs1, destPs1, overwrite: true);
                            Log($"  PackageTemplateFolder Invoke-AppDeployToolkit.ps1 copied → {destPs1}");
                        }
                        else Log($"  TemplateFolder set but Invoke-AppDeployToolkit.ps1 not found in {_pendingTemplateFolder} - skipped.");
                    }
                    Log("Step 4/8 - Patching Invoke-AppDeployToolkit.ps1…");
                    string? invokeScript = hasTemplate
                        ? Directory.GetFiles(path2, "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories).FirstOrDefault()
                        : null;
                    if (invokeScript != null)
                    {
                        PatchSessionParam_Static(invokeScript, "AppName",    $"'{appName}'",    log);
                        PatchSessionParam_Static(invokeScript, "AppVendor",  $"'{appVendor}'",  log);
                        PatchSessionParam_Static(invokeScript, "AppVersion", $"'{appVersion}'", log);
                        PatchSessionParam_Static(invokeScript, "AppRevision",$"'{appRevision}'",log);
                        PatchSessionParam_Static(invokeScript, "AppScriptDate", $"'{DateTime.Now:yyyy-MM-dd}'", log);
                        // InstallName / InstallTitle = package name when enabled in AutoPackage.json
                        if (_cfg.AddInstallName || _cfg.AddInstallTitle)
                        {
                            string pkgNameExe = string.Join("_",
                                new[] { appVendor, appName, appVersion, appRevision }
                                .Select(s => s?.Trim().Replace(" ", "") ?? "")
                                .Where(s => !string.IsNullOrEmpty(s)));
                            if (!string.IsNullOrWhiteSpace(pkgNameExe))
                            {
                                if (_cfg.AddInstallName)
                                    PatchSessionParam_Static(invokeScript, "InstallName",  $"'{pkgNameExe}'", log);
                                if (_cfg.AddInstallTitle)
                                    PatchSessionParam_Static(invokeScript, "InstallTitle", $"'{pkgNameExe}'", log);
                            }
                        }
                        PatchInvokeScriptForExe(invokeScript, exeInstallPath,
                            installArgs, exeUninstallPath, uninstallArgs, log,
                            appName: appName, appVendor: appVendor, appVersion: appVersion,
                            addMsi: _cfg.AddMsiCommandToPs1, addMst: _cfg.AddMstCommandToPs1,
                            addMsp: _cfg.AddMspCommandToPs1, addExe: _cfg.AddExeCommandToPs1,
                            addBat: _cfg.AddBatCommandToPs1, addIss: _cfg.AddIssCommandToPs1,
                            addReg: _cfg.AddRegCommandToPs1, addCer: _cfg.AddCerCommandToPs1,
                            addInf: _cfg.AddInfCommandToPs1, cfg: _cfg);
                        // Always auto-collect from Files\ and merge with template + UI values
                        {
                            string filesDir2 = IOPath.Combine(IOPath.GetDirectoryName(invokeScript)!, "Files");

                            // Read existing values from the ps1 (preserves template values like 'filecompare')
                            string ps1Content = File.Exists(invokeScript) ? File.ReadAllText(invokeScript) : "";
                            var existingProcs2 = new System.Collections.Generic.List<string>();
                            var em2 = System.Text.RegularExpressions.Regex.Match(
                                ps1Content,
                                @"AppProcessesToClose\s*=\s*@\(([^)]*)\)",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (em2.Success)
                                foreach (System.Text.RegularExpressions.Match m in
                                    System.Text.RegularExpressions.Regex.Matches(
                                        em2.Groups[1].Value, @"'([^']+)'"))
                                {
                                    string proc = m.Groups[1].Value.Trim();
                                    if (!string.IsNullOrWhiteSpace(proc))
                                        existingProcs2.Add(proc);
                                }

                            // Start with existing, then add UI closeApps
                            var autoProcs = new System.Collections.Generic.List<string>(existingProcs2);
                            if (!string.IsNullOrWhiteSpace(exeCloseApps))
                                foreach (var p in exeCloseApps.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
                                    if (!autoProcs.Contains(p, StringComparer.OrdinalIgnoreCase))
                                        autoProcs.Add(p);

                            if (Directory.Exists(filesDir2))
                            {
                                // EXEs — use FileVersionInfo ProductName as hint for better registry matching
                                foreach (string ef in Directory.GetFiles(filesDir2, "*.exe", SearchOption.AllDirectories))
                                {
                                    string hint2 = "";
                                    try { var vi2 = System.Diagnostics.FileVersionInfo.GetVersionInfo(ef); hint2 = vi2.ProductName?.Trim() ?? ""; } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                                    if (string.IsNullOrWhiteSpace(hint2)) hint2 = IOPath.GetFileNameWithoutExtension(ef);
                                    var regE = LookupInstalledByExe(ef, hint2);
                                    string procName = DeriveCloseApps(regE);
                                    if (string.IsNullOrWhiteSpace(procName)) procName = IOPath.GetFileNameWithoutExtension(ef);
                                    if (!string.IsNullOrWhiteSpace(procName) &&
                                        !autoProcs.Contains(procName, StringComparer.OrdinalIgnoreCase))
                                        autoProcs.Add(procName);
                                }
                                // MSIs — use ProductName hint for registry lookup (STA COM required)
                                foreach (string mf in Directory.GetFiles(filesDir2, "*.msi", SearchOption.AllDirectories))
                                {
                                    var msiPropsB = ReadMsiPropertiesSTA(mf);
                                    msiPropsB.TryGetValue("ProductCode", out var msiPcB);
                                    msiPropsB.TryGetValue("ProductName", out var msiNameB);
                                    ExeInstalledInfo? regM = !string.IsNullOrWhiteSpace(msiNameB)
                                        ? LookupInstalledByExe("", msiNameB) : null;
                                    if (regM == null && !string.IsNullOrWhiteSpace(msiPcB))
                                        regM = LookupInstalledByProductCode(msiPcB);
                                    string msiProc = DeriveCloseApps(regM);
                                    if (!string.IsNullOrWhiteSpace(msiProc) &&
                                        !autoProcs.Contains(msiProc, StringComparer.OrdinalIgnoreCase))
                                        autoProcs.Add(msiProc);
                                }
                            }
                            if (autoProcs.Count > 0)
                            {
                                string htVal = "@(" + string.Join(", ", autoProcs.Select(p => $"'{p}'")) + ")";
                                PatchSessionParam_Static(invokeScript, "AppProcessesToClose", htVal, log);
                                log.AppendLine($"  EXE CloseApps: {string.Join(", ", autoProcs)}");
                            }
                        }
                        Log("  Patched successfully.");
                    }
                    else Log("  Invoke-AppDeployToolkit.ps1 not found - skipped.");
                    // Copy Files\ from PackageTemplateFolder AFTER patching.
                    // This runs whether or not PSADTTemplateFolder is set — the per-app JSON
                    // PackageTemplateFolder may contain a Files\ folder with additional content
                    // (e.g. transforms, response files) that must always be copied into the package.
                    if (!string.IsNullOrWhiteSpace(_pendingTemplateFolder))
                    {
                        string srcFilesEx = IOPath.Combine(_pendingTemplateFolder, "Files");
                        if (Directory.Exists(srcFilesEx))
                        {
                            // Ensure Files\ destination exists even when no PSADT template was copied
                            if (!Directory.Exists(filesPath)) Directory.CreateDirectory(filesPath);
                            CopyDirectory(srcFilesEx, filesPath);
                            Log($"  PackageTemplateFolder Files\\ copied (post-patch): {srcFilesEx} → {filesPath}");
                        }
                        if (_appTemplateFolderEnabled)
                        {
                            _pendingTemplateFolder    = "";
                            _appTemplateFolderEnabled = false;
                        }
                    }
                    SetProgress(55);
                    if (Paused()) return;

                    // Step 5: Create .intunewin — skipped here, handled by Intunewin tab
                    // The .intunewin is created by NavigateToIntunewinTabAndCreate after Execute
                    // completes (or directly after Package if Execute is not needed).
                    string intunewinPath = "";
                    Log("Step 5/8 - Create intunewin: deferred to Intunewin tab.");
                    SetProgress(75);
                    if (token.IsCancellationRequested) { exeFlowCancelled = true; return; }

                    Log("Step 6/8 - Publish to Intune: skipped (use Publish Package page).");
                    Log("Step 7/8 - Deploy: skipped.");
                    SetProgress(90);

                    // ── Package tab field summary (read on UI thread before Task.Run) ──
                    // All variables already captured above before Task.Run block
                    Log("--- Package Fields ---");
                    Log($"AppName              : {appName}");
                    Log($"AppVendor            : {appVendor}");
                    Log($"AppVersion           : {appVersion}");
                    Log($"AppRevision          : {appRevision}");
                    Log($"AppLang              : EN");
                    Log($"PackageName          : {packageName}");
                    Log($"SourcePath           : {src}");
                    Log($"UninstallPath        : {exeUninstallPath}");
                    Log($"InstallArgs          : {installArgs}");
                    Log($"UninstallArgs        : {uninstallArgs}");
                    Log($"InstallCommand       : {installCmd}");
                    Log($"UninstallCommand     : {uninstallCmd}");

                    // Req: with no PSADT template the Intune Install/Uninstall command IS the source
                    // command computed here (not Invoke-AppDeployToolkit.exe). Persisted via the
                    // per-app save that copies _staticSelectedApp, and used by Publish through it.
                    if (_staticSelectedApp != null && string.IsNullOrWhiteSpace(_staticSelectedApp.PSADTTemplateFolder))
                    {
                        _staticSelectedApp.InstallCommand   = installCmd;
                        _staticSelectedApp.UninstallCommand = uninstallCmd;
                    }
                    Log($"CloseApplications    : {exeCloseApps}");
                    // ── Detection — read all fields on UI thread ───────────────
                    bool isDetFile = false;
                    bool isDetReg  = false;
                    string detFPath = "";
                    string detFName = "";
                    string detFVer  = "";
                    string detFMeth = "";
                    string detFOp   = "";
                    string detRKey  = "";
                    string detRVal  = "";
                    string detRVer  = "";
                    string detRMeth = "";
                    string detROp   = "";
                    // Write File detection block (always write if fields are populated)
                    if (isDetFile || !string.IsNullOrEmpty(detFPath) || !string.IsNullOrEmpty(detFName))
                    {
                        Log($"DetectionType          : File");
                        Log($"Detection.File.Path    : {detFPath}");
                        Log($"Detection.File.Name    : {detFName}");
                        Log($"Detection.File.Method  : {detFMeth}");
                        Log($"Detection.File.Operator: {detFOp}");
                        Log($"Detection.File.Version : {detFVer}");
                    }
                    // Write Registry detection block (always write if fields are populated)
                    if (isDetReg || !string.IsNullOrEmpty(detRKey) || !string.IsNullOrEmpty(detRVal))
                    {
                        Log($"DetectionType          : Registry");
                        Log($"Detection.Reg.KeyPath  : {detRKey}");
                        Log($"Detection.Reg.ValueName: {detRVal}");
                        Log($"Detection.Reg.Method   : {detRMeth}");
                        Log($"Detection.Reg.Operator : {detROp}");
                        Log($"Detection.Reg.Version  : {detRVer}");
                    }
                    Log("--- End Package Fields ---");

                    Log("Step 8/8 - Writing log…");
                    File.AppendAllText(_logPath, log.ToString());
                    SetProgress(100);

                    Dispatcher.Invoke(() =>
                    {
                        SetBusy(false);
                        _busy = false;
                        AutoFlowStatus.Set(AutoFlowStatus.CreatePackage, AutoFlowStatus.State.Ok);
                        ShowSuccessDialog(packageName, path1, _logPath, _createStartTime);
                    });
                });

                if (exeFlowCancelled)
                {
                    try { File.AppendAllText(_logPath, log.ToString()); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                }
            }
            catch (Exception ex)
            {
                Log($"❌ Fatal error: {ex.Message}");
                try { File.AppendAllText(_logPath, log.ToString()); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                try { Dispatcher.Invoke(() => ShowValidationDialog("Auto Package - Error", $"EXE Auto Package failed:\n\n{ex.Message}", "⛔")); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            finally
            {
                _createCts?.Dispose(); _createCts = null;
                _activeIntuneProc = null;
                _creationPaused = false;
                _createPause.Set();
                if (_busy) { Dispatcher.Invoke(() => { SetBusy(false); _busy = false; }); }
            }
        }

        // Helper: patch session params from file path (for EXE flow on background thread)
        private static void PatchSessionParam_Static(string scriptPath, string paramName, string value, StringBuilder log)
        {
            if (!File.Exists(scriptPath)) return;
            string content = File.ReadAllText(scriptPath);
            content = PatchSessionParamContent(content, paramName, value, log);
            File.WriteAllText(scriptPath, content, new UTF8Encoding(true));
        }

        private static string PatchSessionParamContent(string content, string paramName, string value, StringBuilder log)
        {
            var htPattern = new System.Text.RegularExpressions.Regex(
                $@"(?m)^(\s*{System.Text.RegularExpressions.Regex.Escape(paramName)}\s*=\s*).*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (htPattern.IsMatch(content)) { log.AppendLine($"  Updated {paramName} (hashtable)."); return htPattern.Replace(content, $"${{1}}{value}", 1); }
            var swPattern = new System.Text.RegularExpressions.Regex(
                $@"-{System.Text.RegularExpressions.Regex.Escape(paramName)}\s+[^\r\n]+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (swPattern.IsMatch(content)) { log.AppendLine($"  Updated -{paramName} (switch)."); return swPattern.Replace(content, $"-{paramName} {value}", 1); }
            log.AppendLine($"  ⚠ {paramName} not found - skipped.");
            return content;
        }

        // ══════════════════════════════════════════════════════════════════
        //  SUCCESS DIALOG — programmatic WPF (no separate XAML file needed)
        // ══════════════════════════════════════════════════════════════════
        private bool ShowFolderExistsDialog(string folderPath)
        {
            bool result = false;

            var win = new Window
            {
                Title                 = "Auto Package - Folder Exists",
                Width                 = 460,
                MaxWidth              = 460,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = Window.GetWindow(this),
                ResizeMode            = ResizeMode.NoResize,
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = System.Windows.Media.Brushes.Transparent,
                FontFamily            = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar         = false,
            };

            System.Windows.Media.Brush Res(string key) =>
                (System.Windows.Media.Brush?)Application.Current.TryFindResource(key)
                ?? System.Windows.Media.Brushes.Gray;

            var winBg     = Application.Current.TryFindResource("WindowBgBrush")   as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
            var borderClr = Application.Current.TryFindResource("BorderMainBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
            var outerBorder = new Border
            {
                Background      = winBg,
                BorderBrush     = borderClr,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10),
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18,
                    Color = System.Windows.Media.Colors.Black
                }
            };
            win.Content = outerBorder;

            var outerGrid = new Grid();
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerBorder.Child = outerGrid;

            // ── Custom title bar ─────────────────────────────────────────
            var titleBarBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));
            var titleBar = new Border
            {
                Background      = titleBarBg,
                BorderBrush     = borderClr,
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius    = new CornerRadius(10, 10, 0, 0),
                Padding         = new Thickness(16, 6, 12, 6)
            };
            titleBar.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(titleBar, 0);

            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleIcon = new TextBlock { Text = "⚡", FontSize = 14, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var titleText = new TextBlock { Text = "Auto Package - Folder Exists", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            var closeBtn  = new Button   { Content = "✕", FontSize = 11, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            closeBtn.Click += (_, _) => win.Close();

            Grid.SetColumn(titleIcon, 0); Grid.SetColumn(titleText, 1); Grid.SetColumn(closeBtn, 2);
            titleGrid.Children.Add(titleIcon); titleGrid.Children.Add(titleText); titleGrid.Children.Add(closeBtn);
            titleBar.Child = titleGrid;
            outerGrid.Children.Add(titleBar);

            // ── Content ──────────────────────────────────────────────────
            var root = new Grid { Margin = new Thickness(8) };
            Grid.SetRow(root, 1);
            outerGrid.Children.Add(root);

            var boxBg  = Application.Current.TryFindResource("InputBgBrush")       as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF1, 0xF8));
            var boxBdr = Application.Current.TryFindResource("BorderMainBrush")    as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg  = Application.Current.TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x5C, 0x7A));
            var valFg  = Application.Current.TryFindResource("TextPrimaryBrush")   as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1D, 0x2E));

            var details = new Grid();
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Row 0 — Folder label + path box
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var folderLbl = new TextBlock { Text = "Path", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
            var folderBox = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6) };
            folderBox.Child = new TextBlock { Text = folderPath, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = valFg, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
            Grid.SetRow(folderLbl, 0); Grid.SetColumn(folderLbl, 0);
            Grid.SetRow(folderBox, 0); Grid.SetColumn(folderBox, 1);
            details.Children.Add(folderLbl); details.Children.Add(folderBox);

            // Row 1 — spacer
            details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            // Row 2 — message label + message box
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var msgLbl = new TextBlock { Text = "Action", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
            var msgBox = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6) };
            msgBox.Child = new TextBlock { Text = "Folder already exists. Do you want to overwrite it?", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = valFg };
            Grid.SetRow(msgLbl, 2); Grid.SetColumn(msgLbl, 0);
            Grid.SetRow(msgBox, 2); Grid.SetColumn(msgBox, 1);
            details.Children.Add(msgLbl); details.Children.Add(msgBox);

            // Row 3 — spacer
            details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            // Row 4 — Yes / No buttons in col 1, equal width
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Style? sec = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;

            var btnGrid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2 };

            var btnYes = new Button { Content = "✔ Yes", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 4, 0) };
            ApApplyPrimaryLook(btnYes);
            btnYes.Click += (_, _) => { result = true; win.Close(); };

            var btnNo = new Button { Content = "✕ No", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), IsDefault = true };
            btnNo.Click += (_, _) => { result = false; win.Close(); };

            btnGrid.Children.Add(btnYes);
            btnGrid.Children.Add(btnNo);
            Grid.SetRow(btnGrid, 4); Grid.SetColumn(btnGrid, 1);
            details.Children.Add(btnGrid);
            var folderExistsIcon = ApDecisionIcon();
            Grid.SetRow(folderExistsIcon, 4); Grid.SetColumn(folderExistsIcon, 0);
            details.Children.Add(folderExistsIcon);

            root.Children.Add(details);
            win.ShowDialog();
            return result;
        }

        private void ShowSuccessDialog(string packageName, string folderPath, string logPath, DateTime startTime)
        {
            // Sync _localShowMessageBox from SourceDownloadPage.GlobalShowMessageBox via reflection.
            // SourceDownloadPage sets this from the per-app JSON ShowMessageBox field before triggering.
            // No compile-time dependency on SourceDownloadPage.
            try
            {
                var sdType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "SwiftPack.Views.SourceDownloadPage");
                var prop = sdType?.GetProperty("GlobalShowMessageBox",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (prop != null)
                    _localShowMessageBox = (bool)(prop.GetValue(null) ?? true);
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

            var win = new Window
            {
                Title                 = "Auto Package - Complete",
                Width                 = 460,
                MaxWidth              = 460,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = Window.GetWindow(this),
                ResizeMode            = ResizeMode.NoResize,
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = System.Windows.Media.Brushes.Transparent,
                FontFamily            = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar         = false,
            };

            System.Windows.Media.Brush Res(string key) =>
                (System.Windows.Media.Brush?)Application.Current.TryFindResource(key)
                ?? System.Windows.Media.Brushes.Gray;

            // Outer rounded border — gives the window its shape and background
            var winBg     = Application.Current.TryFindResource("WindowBgBrush")   as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
            var borderClr = Application.Current.TryFindResource("BorderMainBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
            var outerBorder = new Border
            {
                Background      = winBg,
                BorderBrush     = borderClr,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10),
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18,
                    Color = System.Windows.Media.Colors.Black
                }
            };
            win.Content = outerBorder;

            var outerGrid = new Grid();
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // custom title bar
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // content
            outerBorder.Child = outerGrid;

            // ── Custom title bar ─────────────────────────────────────────
            var titleBarBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));
            var titleBar = new Border
            {
                Background      = titleBarBg,
                BorderBrush     = borderClr,
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius    = new CornerRadius(10, 10, 0, 0),
                Padding         = new Thickness(16, 6, 12, 6)
            };
            titleBar.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(titleBar, 0);

            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // ⚡ icon
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // title text
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // ✕ close

            var titleIcon = new TextBlock
            {
                Text = "⚡", FontSize = 14,
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var titleText = new TextBlock
            {
                Text = "Auto Package - Complete",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            var closeBtn = new Button
            {
                Content = "✕", FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 2, 6, 2)
            };
            closeBtn.Click += (_, _) => win.Close();

            Grid.SetColumn(titleIcon, 0);
            Grid.SetColumn(titleText, 1);
            Grid.SetColumn(closeBtn,  2);
            titleGrid.Children.Add(titleIcon);
            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeBtn);
            titleBar.Child = titleGrid;
            outerGrid.Children.Add(titleBar);

            // ── Content area ─────────────────────────────────────────────
            var root = new Grid { Margin = new Thickness(8) };
            for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(root, 1);
            outerGrid.Children.Add(root);

            // ── Single grid — ✓+timing, Location box, Log File box, buttons ──────
            // Col 0 = label (72px), Col 1 = value (star)
            // Timing row uses col 0 for ✓ and col 1 for the time text
            // Buttons row spans col 1 only, centered within it
            var details = new Grid { Margin = new Thickness(0) };
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(details, 0);
            Grid.SetRowSpan(details, 5); // occupies all root rows

            var endTime  = DateTime.Now;
            var elapsed  = endTime - startTime;
            string elapsedStr = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                : elapsed.TotalSeconds >= 1
                    ? $"{(int)elapsed.TotalSeconds}s"
                    : $"{(int)elapsed.TotalMilliseconds}ms";

            var boxBg  = Application.Current.TryFindResource("InputBgBrush")       as System.Windows.Media.Brush
                         ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF1, 0xF8));
            var boxBdr = Application.Current.TryFindResource("BorderMainBrush")    as System.Windows.Media.Brush
                         ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg  = Application.Current.TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush
                         ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x5C, 0x7A));
            var valFg  = Application.Current.TryFindResource("TextPrimaryBrush")   as System.Windows.Media.Brush
                         ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1D, 0x2E));

            // Row 0 — Time label (col 0) + timing rounded box (col 1)
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var timeLbl = new TextBlock
            {
                Text = "Time",
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = lblFg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0)
            };
            var timeBox = new Border
            {
                Background      = boxBg,
                BorderBrush     = boxBdr,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(10, 6, 10, 6)
            };
            timeBox.Child = new TextBlock
            {
                Text         = $"Started: {startTime:HH:mm:ss}  Finished: {endTime:HH:mm:ss}  Duration: {elapsedStr}",
                FontSize     = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground   = valFg,
                FontFamily   = new System.Windows.Media.FontFamily("Consolas")
            };
            Grid.SetRow(timeLbl, 0); Grid.SetColumn(timeLbl, 0);
            Grid.SetRow(timeBox, 0); Grid.SetColumn(timeBox, 1);
            details.Children.Add(timeLbl);
            details.Children.Add(timeBox);

            // Row 1 — spacer
            details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            // Rows 2,3,4,5 — Location and Log File boxes
            // Resolve the subfolder that contains Invoke-AppDeployToolkit.ps1 (e.g. Native\)
            // to open and display that instead of the top-level package folder.
            string nativeFolderPath = folderPath;
            try
            {
                string? ps1 = Directory.GetFiles(folderPath, "Invoke-AppDeployToolkit.ps1",
                                  SearchOption.AllDirectories).FirstOrDefault();
                if (ps1 != null) nativeFolderPath = IOPath.GetDirectoryName(ps1)!;
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

            var dataRows = new[] { ("Path", nativeFolderPath), ("Log", logPath) };
            for (int i = 0; i < dataRows.Length; i++)
            {
                details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (i < dataRows.Length - 1)
                    details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
                int r = 2 + i * 2;
                var lbl = new TextBlock
                {
                    Text = dataRows[i].Item1,
                    FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = lblFg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0)
                };
                var box = new Border
                {
                    Background      = boxBg,
                    BorderBrush     = boxBdr,
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(6),
                    Padding         = new Thickness(10, 6, 10, 6)
                };
                box.Child = new TextBlock
                {
                    Text         = dataRows[i].Item2,
                    FontSize     = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground   = valFg,
                    FontFamily   = new System.Windows.Media.FontFamily("Consolas")
                };
                Grid.SetRow(lbl, r); Grid.SetColumn(lbl, 0);
                Grid.SetRow(box, r); Grid.SetColumn(box, 1);
                details.Children.Add(lbl);
                details.Children.Add(box);
            }

            // Row 6 — spacer before buttons
            details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            // Row 7 — buttons in col 1, equal width filling the full column via UniformGrid
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Style? sec = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;

            var btnGrid = new System.Windows.Controls.Primitives.UniformGrid
            {
                Rows = 1, Columns = 3
            };

            var bFolder = new Button { Content = "📁 Open Path", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 4, 0) };
            ApApplyPrimaryLook(bFolder);
            bFolder.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", nativeFolderPath) { UseShellExecute = true }); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } };

            var bLog = new Button { Content = "📄 Open Log", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 4, 0) };
            bLog.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true }); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } };

            var ok = new Button { Content = "✕ Close", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), IsDefault = true };
            ok.Click += (_, _) => win.Close();

            btnGrid.Children.Add(bFolder);
            btnGrid.Children.Add(bLog);
            btnGrid.Children.Add(ok);

            Grid.SetRow(btnGrid, 7); Grid.SetColumn(btnGrid, 1);
            details.Children.Add(btnGrid);
            var completeIcon = ApDecisionIcon();
            Grid.SetRow(completeIcon, 7); Grid.SetColumn(completeIcon, 0);
            details.Children.Add(completeIcon);

            root.Children.Add(details);

            // Auto-close if GlobalShowMessageBox = No; always show on failure (handled separately)
            if (!_localShowMessageBox)
            {
                win.Loaded += (_, _) => win.Dispatcher.BeginInvoke(
                    new Action(() => win.Close()),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            win.ShowDialog();

            // Works for both MSI and EXE tab since ShowSuccessDialog is shared
            LastCreatedPackageFolder = _packageRootPath;

            // Capture Intunewin/Publish trigger state and clear flags
            bool   doIntunewinTab = _triggerIntunewinTab;
            bool   doPublishTab   = _triggerPublishTab;
            string pkgFolder      = _packageRootPath;
            string capName        = _publishCapAppName;
            string capPub         = _publishCapPublisher;
            string capVer         = _publishCapVersion;
            string capDesc        = _publishCapDesc;
            _triggerIntunewinTab  = false;
            _triggerPublishTab    = false;

            // ── Deferred "Uninstall source silently" — runs AFTER Create Package ──────────
            // Required order (ALL paths):
            //     Install source → Create package → Uninstall source → Install package
            //                    → Uninstall package → Intunewin → Publish → ...
            //
            // SourceDownloadPage deferred the source uninstall (it was NOT passed to
            // RunPsadtCommandAndThenAutoPackage). We run it HERE, synchronously, right after
            // Create Package completes (the success dialog above is shown only once the
            // package — including copy-source-to-Files — is finished) and BEFORE any
            // downstream step (Install package / Uninstall package / Intunewin / Publish),
            // which are all triggered by `afterCreate` / `intunewinThenPublish` further below.
            //
            // We consume (and clear) the deferred command/flags here so the downstream
            // Intunewin/Publish code does NOT run it a second time.
            //
            // FAILURE RULE: if the source uninstall fails, delete the just-created package
            // folder and set sourceUninstallFailed=true. The afterCreate / intunewinThenPublish
            // invocation at the bottom of this method is then SKIPPED, so Install package and
            // everything after it does NOT run.
            bool sourceUninstallFailed = false;
            // R4: the source uninstall now runs on a background thread (see below) so the UI
            // does not freeze for 10–15 s during an MSI uninstall. These are pre-declared so the
            // FireChain() local function (which runs the downstream Intunewin/Publish chain) can
            // be invoked from the uninstall's completion continuation as well as synchronously.
            bool    uninstallRunningAsync = false;
            Action? afterCreate           = null;
            Action? intunewinThenPublish  = null;
            {
                // Local logger — ShowSuccessDialog has no in-scope Log(); write straight
                // to the unified log file passed in via `logPath`.
                void WriteUninstLog(string m)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(logPath))
                            System.IO.File.AppendAllText(logPath,
                                $"[{DateTime.Now:HH:mm:ss}] {m}{Environment.NewLine}");
                    }
                    catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }

                // Snapshot + clear the deferred-uninstall fields so they can't be
                // consumed twice (and so a later run starts clean).
                string deferredSrcCmd   = _staticDeferredUninstallSourceCmd;
                _staticDeferredUninstallSourceCmd = "";
                bool   deferredLazy     = _staticDeferredUninstallSourceLazy;
                string deferredLazyArgs = _staticDeferredUninstallSourceLazyArgs;
                _staticDeferredUninstallSourceLazy     = false;
                _staticDeferredUninstallSourceLazyArgs = "";

                if (!string.IsNullOrWhiteSpace(deferredSrcCmd) || deferredLazy)
                {
                    // ── Auto-prefill Publish → Detection BEFORE the source app is uninstalled ──
                    // A deferred "Uninstall source silently" is about to run (below) and will
                    // remove the app — and with it the app's Uninstall registry entry. The
                    // Publish → Detection rules (File / Registry / MSI) are resolved from that
                    // registry entry, so they MUST be captured NOW, while the app is still
                    // installed (Create Package has just completed — i.e. right after Step 5/8).
                    //
                    //   • prefillOnly:true populates the AutoPublishPage detection fields and
                    //     persists the resolved values into _staticSelectedApp (in memory only).
                    //     It does NOT switch tabs and does NOT write any JSON. The later Publish
                    //     navigation (after Intunewin) merges _staticSelectedApp so the Save in
                    //     the Publish tab writes the captured detection values.
                    //   • Nothing is written to the per-app template JSON — the captured values
                    //     live only in memory until Publish Save writes Intune\PackageName.json.
                    //
                    // Runs only when Intunewin or Publish is part of the flow (otherwise there is
                    // no Detection tab to fill). For the package Install/Uninstall path the app is
                    // (re)installed via the Execute tab AFTER this point, so that path keeps its
                    // own onAfterInstall prefill — this block only fires when a SOURCE uninstall
                    // is queued, i.e. the app is installed right now.
                    if ((doPublishTab || doIntunewinTab) &&
                        !string.IsNullOrWhiteSpace(_packageRootPath) &&
                        Directory.Exists(_packageRootPath))
                    {
                        try
                        {
                            WriteUninstLog("Auto-prefill Detection (Publish) - capturing File/Registry/MSI before source uninstall…");
                            NavigateToPublishTab(_packageRootPath,
                                appName:     capName,
                                appVendor:   capPub,
                                appVersion:  capVer,
                                description: capDesc,
                                prefillOnly: true);

                            // Record what was captured into the AutoPackage log (existing file).
                            var sa = _staticSelectedApp;
                            if (sa != null)
                            {
                                WriteUninstLog("--- Auto-prefill Detection ---");
                                if (!string.IsNullOrWhiteSpace(sa.DetectionMsiProductCode))
                                {
                                    WriteUninstLog($"  Detection.MSI.ProductCode : {sa.DetectionMsiProductCode}");
                                    WriteUninstLog($"  Detection.MSI.Value       : {sa.DetectionMsiValue}");
                                }
                                WriteUninstLog($"  Detection.File.FolderPath : {sa.DetectionFileFolderPath}");
                                WriteUninstLog($"  Detection.File.Name       : {sa.DetectionFileFileName}");
                                WriteUninstLog($"  Detection.File.Value      : {sa.DetectionFileValue}");
                                WriteUninstLog($"  Detection.Reg.KeyPath     : {sa.DetectionRegKeyPath}");
                                WriteUninstLog($"  Detection.Reg.ValueName   : {sa.DetectionRegValueName}");
                                WriteUninstLog($"  Detection.Reg.Value       : {sa.DetectionRegistryValue}");
                                WriteUninstLog("--- End Auto-prefill Detection ---");
                            }

                            // Capture the uninstall-registry details for the Document tab NOW,
                            // while the app is still installed (it is uninstalled just below).
                            CaptureUninstallRegistryForDocument();
                        }
                        catch (Exception exPre)
                        {
                            WriteUninstLog($"Auto-prefill Detection failed: {exPre.Message}");
                        }
                    }
                    // ── end auto-prefill Detection ─────────────────────────────────────────────

                    // Resolve the lazy (EXE) command from the uninstall-source field that
                    // LookupInstalledByExe populated after the silent install ran.
                    string cmdToRun = deferredSrcCmd;
                    if (string.IsNullOrWhiteSpace(cmdToRun) && deferredLazy)
                    {
                        string uExe  = ExeTxtUninstallSource?.Text.Trim() ?? "";
                        string uiArg = ExeTxtUninstallArgs?.Text.Trim()   ?? "";
                        string uArgs = !string.IsNullOrWhiteSpace(uiArg) ? uiArg : deferredLazyArgs;

                        // Fallback: if the UI field wasn't populated (e.g. LookupInstalledByExe
                        // didn't run or didn't match), resolve the uninstaller directly from the
                        // registry by app name — the source app is still installed at this point
                        // (Create Package ran while it was installed), so the UninstallString exists.
                        if (string.IsNullOrWhiteSpace(uExe))
                        {
                            string nameHint = ExeTxtAppName?.Text.Trim() ?? "";
                            if (string.IsNullOrWhiteSpace(nameHint))
                                nameHint = _staticSelectedApp?.SourceAppName ?? "";
                            try
                            {
                                var regInfo = LookupInstalledByExe("", nameHint);
                                string uninstStr = !string.IsNullOrWhiteSpace(regInfo?.QuietUninstallString)
                                    ? regInfo!.QuietUninstallString
                                    : (regInfo?.UninstallString ?? "");
                                if (!string.IsNullOrWhiteSpace(uninstStr))
                                {
                                    // Parse "exe args" (honour quoted path)
                                    if (uninstStr.StartsWith("\""))
                                    {
                                        int end = uninstStr.IndexOf('"', 1);
                                        if (end > 1)
                                        {
                                            uExe = uninstStr.Substring(1, end - 1).Trim();
                                            string regArgs = uninstStr.Substring(end + 1).Trim();
                                            if (string.IsNullOrWhiteSpace(uArgs)) uArgs = regArgs;
                                        }
                                    }
                                    else
                                    {
                                        var parts = uninstStr.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                                        uExe = parts.Length > 0 ? parts[0] : "";
                                        if (string.IsNullOrWhiteSpace(uArgs) && parts.Length > 1) uArgs = parts[1];
                                    }
                                }
                            }
                            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        }

                        if (!string.IsNullOrWhiteSpace(uExe))
                            cmdToRun = string.IsNullOrWhiteSpace(uArgs)
                                ? $"\"{uExe}\""
                                : $"\"{uExe}\" {uArgs}";
                    }

                    if (!string.IsNullOrWhiteSpace(cmdToRun))
                    {
                        // Split "exe args" — honour a leading quoted path.
                        string exe = cmdToRun, args = "";
                        if (cmdToRun.StartsWith("\""))
                        {
                            int closeQ = cmdToRun.IndexOf('"', 1);
                            if (closeQ > 0) { exe = cmdToRun.Substring(1, closeQ - 1); args = cmdToRun.Substring(closeQ + 1).Trim(); }
                        }
                        else
                        {
                            int sp = cmdToRun.IndexOf(' ');
                            if (sp > 0) { exe = cmdToRun.Substring(0, sp); args = cmdToRun.Substring(sp + 1).Trim(); }
                        }

                        string pkgFolderToCleanup = _packageRootPath; // set above: LastCreatedPackageFolder = _packageRootPath
                        WriteUninstLog($"Uninstall source silently - running AFTER Create Package.");
                        WriteUninstLog($"  Executable : {exe}");
                        WriteUninstLog($"  Arguments  : {args}");

                        // ── Run the source uninstall WITHOUT blocking the UI thread (R4) ─────────
                        // Previously proc.WaitForExit() ran on the UI thread and froze the whole app
                        // for 10–15 s while msiexec (MSI source) uninstalled. Run the process on a
                        // background thread; when it exits, marshal back to the UI thread to evaluate
                        // success, clean up on failure, then fire the downstream chain via FireChain().
                        // The detection auto-prefill above already captured everything BEFORE this
                        // point, so uninstalling the app in the background loses no detection data.
                        string exeC = exe;
                        string argsC = args;
                        string pkgC = pkgFolderToCleanup;
                        uninstallRunningAsync = true;
                        // Show the Download-tab thin progress bar + highlight "Uninstall source
                        // silently" (the host navigates back to the Download tab). This is the
                        // deferred uninstall-after-create path (otherwise we'd sit on the Package tab).
                        try { ForwardSilentPhaseToDownload("BeginSilentPhase", "uninstall"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        var _uninSrcStart = DateTime.Now;
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            var psi = new System.Diagnostics.ProcessStartInfo(exeC, argsC)
                            {
                                UseShellExecute = true,   // allow UAC elevation for msiexec/uninstaller
                                Verb            = "runas"
                            };
                            var proc = System.Diagnostics.Process.Start(psi);
                            proc?.WaitForExit();
                            return proc?.ExitCode ?? -1;
                        })
                        .ContinueWith(t => Dispatcher.Invoke(() =>
                        {
                            bool faulted  = t.IsFaulted;
                            int  exitCode = faulted ? -1 : t.Result;
                            // Hide the Download-tab progress bar + clear the highlight now that
                            // the deferred uninstall has finished (success or failure).
                            try { ForwardSilentPhaseToDownload("EndSilentPhase"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                            // 0 = success, 3010 = success but reboot required (MSI), 1605 = product not installed (treat as ok)
                            bool uninstallOk = !faulted && (exitCode == 0 || exitCode == 3010 || exitCode == 1605);
                            AutoFlowStatus.Set(AutoFlowStatus.UninstallSource, uninstallOk ? AutoFlowStatus.State.Ok : AutoFlowStatus.State.Failed);
                            if (faulted)
                                WriteUninstLog($"Uninstall source silently FAILED: {t.Exception?.GetBaseException().Message}");
                            else
                                WriteUninstLog($"Uninstall source silently completed. Exit code: {exitCode} → {(uninstallOk ? "OK" : "FAILED")}");

                            // Status dialog (only when ShowMessageBox = Yes).
                            if (AutoShowMessageBox.Value)
                            {
                                try
                                {
                                    var _uEnd = DateTime.Now; var _uEl = _uEnd - _uninSrcStart;
                                    string _uEs = _uEl.TotalMinutes >= 1 ? $"{(int)_uEl.TotalMinutes}m {_uEl.Seconds}s"
                                                : _uEl.TotalSeconds >= 1 ? $"{(int)_uEl.TotalSeconds}s" : $"{(int)_uEl.TotalMilliseconds}ms";
                                    // Path row: for MSI show "msiexec /x {GUID}", for EXE show the
                                    // uninstaller's full path (e.g. C:\Program Files\Smekal\FileCompare\uninstall.exe).
                                    string _uPath;
                                    bool _isMsiUn = IOPath.GetFileName(exeC).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
                                                 || IOPath.GetFileName(exeC).Equals("msiexec", StringComparison.OrdinalIgnoreCase);
                                    if (_isMsiUn)
                                    {
                                        // Reduce args to the "/x {code}" portion for a clean display.
                                        string _xPart = argsC;
                                        int _xi = argsC.IndexOf("/x", StringComparison.OrdinalIgnoreCase);
                                        if (_xi >= 0)
                                        {
                                            string _after = argsC.Substring(_xi);
                                            int _sp = _after.IndexOf(' ', 3);
                                            _xPart = _sp > 0 ? _after.Substring(0, _sp) : _after;
                                        }
                                        _uPath = $"msiexec {_xPart}".Trim();
                                    }
                                    else
                                    {
                                        _uPath = exeC;
                                    }
                                    var urows = new System.Collections.Generic.List<(string,string,bool)>
                                    {
                                        ("Time", $"Started: {_uninSrcStart:HH:mm:ss}  Finished: {_uEnd:HH:mm:ss}  Duration: {_uEs}", false),
                                        ("Path", _uPath, false),
                                        ("Exit Code", exitCode.ToString(), true),
                                    };
                                    AutoStatusDialog.Show(this,
                                        uninstallOk ? "Uninstall Source Silently - Complete" : "Uninstall Source Silently - Failed",
                                        "↥", uninstallOk, urows);
                                }
                                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                            }

                            // ── On failure: delete the package folder and STOP the chain ──
                            if (!uninstallOk)
                            {
                                sourceUninstallFailed = true;   // suppress Install package + everything after
                                // The created package folder is deleted below, so Create Package is no
                                // longer a standalone success - reset it to neutral so it does NOT paint
                                // green (which would wrongly imply the package still exists) while
                                // "Uninstall source silently" is shown red.
                                AutoFlowStatus.Set(AutoFlowStatus.CreatePackage, AutoFlowStatus.State.None);
                                if (!string.IsNullOrWhiteSpace(pkgC) && Directory.Exists(pkgC))
                                {
                                    try
                                    {
                                        Directory.Delete(pkgC, recursive: true);
                                        WriteUninstLog($"Uninstall failed - deleted package folder: {pkgC}");
                                        LastCreatedPackageFolder = "";   // nothing valid to hand downstream
                                    }
                                    catch (Exception delEx)
                                    {
                                        WriteUninstLog($"Uninstall failed AND could not delete package folder " +
                                            $"'{pkgC}': {delEx.Message}");
                                    }
                                }
                                // Reset the auto-flow message-box suppression so the UI behaves normally
                                // after we abort the chain.
                                _localShowMessageBox = true;
                            }

                            // The source uninstall has finished — now run the downstream chain.
                            FireChain();
                        }));
                    }
                }
            }
            // ── end deferred "Uninstall source silently" ───────────────────────────────

            // Build the Intunewin→Publish tail action.
            // Publish is stored separately so IwBtnCreate_Click fires it after intunewin completes,
            // rather than navigating immediately when NavigateToIntunewinTabAndCreate is called.

            // publishAction reads _pendingAfterPublishUninstallExecute and
            // _staticDeferredUninstallSourceCmd LAZILY when it executes (after Intunewin completes).
            // By that time _onAfterCreateComplete has already run and set those fields.
            // It fires deferredExec directly via BeginInvoke(Normal) after NavigateToPublishTab.

            Action? publishAction = doPublishTab
                ? () =>
                  {
                      // Read deferred fields lazily — _onAfterCreateComplete has run by now.
                      Action? deferredExec = _pendingAfterPublishUninstallExecute;
                      _pendingAfterPublishUninstallExecute = null;
                      // If uninstall is pending, suppress auto-trigger in NavigateToPublishTab.

                      // "Uninstall source silently" deferred command — run directly as a silent
                      // process after Publish, NOT through the Execute tab UI. This matches what
                      // RunPsadtCommandAndThenAutoPackage does: shell the command with no window.
                      string deferredUninstallSrcCmd = _staticDeferredUninstallSourceCmd;
                      _staticDeferredUninstallSourceCmd = "";

                      // Lazy resolution: SourceDownloadPage set the lazy flag because the app
                      // wasn't installed yet when the deferral was registered (so the registry
                      // lookup found nothing and the command couldn't be built).  By now
                      // LookupInstalledByExe has run (post-install) and populated
                      // ExeTxtUninstallSource with the correct dedicated uninstaller path.
                      if (string.IsNullOrWhiteSpace(deferredUninstallSrcCmd) && _staticDeferredUninstallSourceLazy)
                      {
                          string lazyArgs = _staticDeferredUninstallSourceLazyArgs;
                          _staticDeferredUninstallSourceLazy     = false;
                          _staticDeferredUninstallSourceLazyArgs = "";
                          string uExe  = ExeTxtUninstallSource?.Text.Trim() ?? "";
                          string uArgs = !string.IsNullOrWhiteSpace(ExeTxtUninstallArgs?.Text.Trim())
                                         ? ExeTxtUninstallArgs!.Text.Trim()
                                         : lazyArgs;
                          if (!string.IsNullOrWhiteSpace(uExe))
                              deferredUninstallSrcCmd = string.IsNullOrWhiteSpace(uArgs)
                                  ? $"\"{uExe}\""
                                  : $"\"{uExe}\" {uArgs}";
                      }
                      else
                      {
                          _staticDeferredUninstallSourceLazy     = false;
                          _staticDeferredUninstallSourceLazyArgs = "";
                      }

                      // Capture auto-trigger flags BEFORE NavigateToPublishTab consumes _autoTriggerSave.
                      // The publish trigger must fire here — inside publishAction — NOT inside
                      // NavigateToPublishTab, because NavigateToPublishTab is also called with
                      // prefillOnly=true during the Execute/Install phase and from BtnTabPublish_Click.
                      // Firing from publishAction guarantees the trigger only runs after Intunewin
                      // creation has completed and _lastCreatedIntunewinPath has been set.
                      bool shouldAutoPublish = _autoTriggerPublish;
                      bool willAutoSave      = _autoTriggerSave;   // consumed inside NavigateToPublishTab

                      // PublishToIntune=Yes → the publish-to-Intune script (fired below) is the
                      // true terminus. Set this BEFORE NavigateToPublishTab so its Save block
                      // does NOT prematurely raise all-complete.
                      if (shouldAutoPublish && _autoPublishPage != null)
                          _publishToIntuneIsTerminus = true;

                      // Navigate to Publish tab — populate all fields + register OnPublishRequested.
                      //     inside _pendingAfterPublishUninstallExecute (set in BtnCreate_Click)
                      NavigateToPublishTab(pkgFolder, capName, capPub, capVer, capDesc);

                      // Auto-trigger Publish button (PublishToIntune=Yes).
                      // willAutoSave drives the delay so Save finishes writing the JSON
                      // before the publish PowerShell script reads it.
                      if (shouldAutoPublish && _autoPublishPage != null)
                      {
                          _autoTriggerPublish = false;
                          var pg = _autoPublishPage;
                          string capturedIwPath = _lastCreatedIntunewinPath;
                          int publishDelay = willAutoSave ? 1500 : 800;
                          try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] TriggerPublish - PublishToIntune=Yes (delay {publishDelay}ms){Environment.NewLine}"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                          System.Threading.Tasks.Task.Delay(publishDelay).ContinueWith(_ =>
                              Dispatcher.Invoke(() =>
                              {
                                  if (!string.IsNullOrWhiteSpace(capturedIwPath) &&
                                      string.IsNullOrWhiteSpace(pg.IntunewinFilePath))
                                      pg.SetIntunewinFilePath(capturedIwPath);
                                  pg.TriggerPublishButton();
                              }));
                      }

                      // Fire Execute Uninstall (Uninstall package) after Publish tab is shown.
                      if (deferredExec != null)
                      {
                          Action capturedExec = deferredExec;
                          Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal,
                              new Action(capturedExec));
                      }

                      // Run "Uninstall source silently" command directly as a hidden process.
                      // This is independent of the Execute tab — no UI, no dialog.
                      if (!string.IsNullOrWhiteSpace(deferredUninstallSrcCmd))
                      {
                          string capturedCmd = deferredUninstallSrcCmd;
                          Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(() =>
                          {
                              try
                              {
                                  // Split exe + args. Handles:
                                  //   msiexec.exe /x {GUID} /qn
                                  //   "C:\path\uninstaller.exe" /S
                                  string exe  = capturedCmd;
                                  string args = "";
                                  if (capturedCmd.StartsWith("\""))
                                  {
                                      int closeQ = capturedCmd.IndexOf('"', 1);
                                      if (closeQ > 0)
                                      {
                                          exe  = capturedCmd.Substring(1, closeQ - 1);
                                          args = capturedCmd.Substring(closeQ + 1).Trim();
                                      }
                                  }
                                  else
                                  {
                                      int firstSpace = capturedCmd.IndexOf(' ');
                                      if (firstSpace > 0)
                                      {
                                          exe  = capturedCmd.Substring(0, firstSpace);
                                          args = capturedCmd.Substring(firstSpace + 1).Trim();
                                      }
                                  }
                                  var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
                                  {
                                      UseShellExecute = false,
                                      CreateNoWindow  = true,
                                      WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden
                                  };
                                  // Show the Download-tab progress bar + highlight while this
                                  // best-effort source uninstall runs; end it when the process exits.
                                  try { ForwardSilentPhaseToDownload("BeginSilentPhase", "uninstall"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                                  var _ux2Start = DateTime.Now;
                                  var up = System.Diagnostics.Process.Start(psi);
                                  if (up != null)
                                  {
                                      System.Threading.Tasks.Task.Run(() => { try { up.WaitForExit(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } })
                                          .ContinueWith(_ => Dispatcher.Invoke(() =>
                                          {
                                              try { ForwardSilentPhaseToDownload("EndSilentPhase"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                                              int uxExit = -1; try { uxExit = up.ExitCode; } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                                              bool uxOk = (uxExit == 0 || uxExit == 3010 || uxExit == 1605);
                                              if (AutoShowMessageBox.Value)
                                              {
                                                  try
                                                  {
                                                      var _ux2End = DateTime.Now; var _ux2El = _ux2End - _ux2Start;
                                                      string _ux2Es = _ux2El.TotalMinutes >= 1 ? $"{(int)_ux2El.TotalMinutes}m {_ux2El.Seconds}s"
                                                                    : _ux2El.TotalSeconds >= 1 ? $"{(int)_ux2El.TotalSeconds}s" : $"{(int)_ux2El.TotalMilliseconds}ms";
                                                      string _ux2Path;
                                                      bool _ux2Msi = IOPath.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
                                                                  || IOPath.GetFileName(exe).Equals("msiexec", StringComparison.OrdinalIgnoreCase);
                                                      if (_ux2Msi)
                                                      {
                                                          string _xp = args;
                                                          int _xi = args.IndexOf("/x", StringComparison.OrdinalIgnoreCase);
                                                          if (_xi >= 0)
                                                          {
                                                              string _after = args.Substring(_xi);
                                                              int _sp = _after.IndexOf(' ', 3);
                                                              _xp = _sp > 0 ? _after.Substring(0, _sp) : _after;
                                                          }
                                                          _ux2Path = $"msiexec {_xp}".Trim();
                                                      }
                                                      else { _ux2Path = exe; }
                                                      AutoStatusDialog.Show(this,
                                                          uxOk ? "Uninstall Source Silently - Complete" : "Uninstall Source Silently - Failed",
                                                          "↥", uxOk,
                                                          new System.Collections.Generic.List<(string,string,bool)>
                                                          {
                                                              ("Time", $"Started: {_ux2Start:HH:mm:ss}  Finished: {_ux2End:HH:mm:ss}  Duration: {_ux2Es}", false),
                                                              ("Path", _ux2Path, false),
                                                              ("Exit Code", uxExit.ToString(), true),
                                                          });
                                                  }
                                                  catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                                              }
                                          }));
                                  }
                                  else
                                  {
                                      try { ForwardSilentPhaseToDownload("EndSilentPhase"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                                  }
                              }
                              catch { /* silent - uninstall source is best-effort */ }
                          }));
                      }

                      // If neither deferredExec nor deferredUninstallSrcCmd is set,
                      // Publish is the last step — reset GlobalShowMessageBox now.
                      if (deferredExec == null && string.IsNullOrWhiteSpace(deferredUninstallSrcCmd))
                          _localShowMessageBox = true;
                  }
                : (Action?)null;

            // Signal IwBtnCreate_Click to keep GlobalShowMessageBox false when there will be
            // a deferred Execute Uninstall after Publish. We know this NOW because:
            //   _pendingAfterPublishUninstallExecute will be set by _onAfterCreateComplete (async)
            //   _staticDeferredUninstallSourceCmd may already be set by SourceDownloadPage.
            // We use a conservative check: if either uninstall flag is potentially deferred,
            // set _publishActionHasDeferredUninstall = true so IwBtnCreate_Click preserves the flag.
            // publishAction itself resets GlobalShowMessageBox if it turns out nothing is deferred.
            _publishActionHasDeferredUninstall = doPublishTab &&
                (_pendingAfterPublishUninstallExecute != null ||
                 !string.IsNullOrWhiteSpace(_staticDeferredUninstallSourceCmd) ||
                 _staticDeferredUninstallSourceLazy);

            intunewinThenPublish = null;
            if (doIntunewinTab || doPublishTab)
            {
                intunewinThenPublish = () =>
                {
                    // ── Deferred source-uninstall (UninstallSourceSilently path) ─────────
                    // Scenario 2: InstallSourceSilently=Yes + UninstallSourceSilently=Yes
                    //             + CreatePackage=Yes + CreateIntunewin=Yes
                    //             + InstallPackage=No + UninstallPackage=No
                    //
                    // SourceDownloadPage set _staticDeferredUninstallSourceCmd because
                    // _doCreateIw=true and doUninstallPkg=false. There is NO package-execute
                    // step (_onAfterCreateComplete has captureInstall=captureUninstall=false
                    // and calls afterExec directly, landing here).
                    //
                    // Required order:  Source Install → Create Package → Source Uninstall
                    //                  → Intunewin tab
                    //
                    // We run the source-uninstall silently NOW (fire-and-forget, like a
                    // background process), wait briefly for it to start, then navigate to
                    // the Intunewin tab. The .intunewin creation itself takes several seconds,
                    // so the uninstall completes in parallel — the app is gone before the
                    // .intunewin file is needed by Intune.
                    //
                    // When Publish IS active, publishAction already consumes the deferred
                    // source-uninstall cmd — do not double-run it here.
                    // ─────────────────────────────────────────────────────────────────────
                    if (!doPublishTab)
                    {
                        string deferredSrcCmd   = _staticDeferredUninstallSourceCmd;
                        _staticDeferredUninstallSourceCmd = "";
                        bool   deferredLazy     = _staticDeferredUninstallSourceLazy;
                        string deferredLazyArgs = _staticDeferredUninstallSourceLazyArgs;
                        _staticDeferredUninstallSourceLazy     = false;
                        _staticDeferredUninstallSourceLazyArgs = "";

                        if (!string.IsNullOrWhiteSpace(deferredSrcCmd) || deferredLazy)
                        {
                            // Resolve lazy command (EXE uninstall path populated post-install)
                            string cmdToRun = deferredSrcCmd;
                            if (string.IsNullOrWhiteSpace(cmdToRun) && deferredLazy)
                            {
                                string uExe  = Dispatcher.Invoke(() => ExeTxtUninstallSource?.Text.Trim() ?? "");
                                string uArgs = !string.IsNullOrWhiteSpace(Dispatcher.Invoke(() => ExeTxtUninstallArgs?.Text.Trim() ?? ""))
                                               ? Dispatcher.Invoke(() => ExeTxtUninstallArgs!.Text.Trim())
                                               : deferredLazyArgs;
                                if (!string.IsNullOrWhiteSpace(uExe))
                                    cmdToRun = string.IsNullOrWhiteSpace(uArgs)
                                        ? $"\"{uExe}\""
                                        : $"\"{uExe}\" {uArgs}";
                            }

                            if (!string.IsNullOrWhiteSpace(cmdToRun))
                            {
                                // Run source uninstall silently BEFORE Intunewin — fire and forget.
                                // The uninstall runs in the background; Intunewin creation starts
                                // immediately after. Both finish before Intune publish begins.
                                try
                                {
                                    string exe = cmdToRun, args = "";
                                    if (cmdToRun.StartsWith("\""))
                                    {
                                        int closeQ = cmdToRun.IndexOf('"', 1);
                                        if (closeQ > 0) { exe = cmdToRun.Substring(1, closeQ - 1); args = cmdToRun.Substring(closeQ + 1).Trim(); }
                                    }
                                    else
                                    {
                                        int sp = cmdToRun.IndexOf(' ');
                                        if (sp > 0) { exe = cmdToRun.Substring(0, sp); args = cmdToRun.Substring(sp + 1).Trim(); }
                                    }
                                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args)
                                    {
                                        UseShellExecute = false,
                                        CreateNoWindow  = true,
                                        WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden
                                    });
                                }
                                catch { /* silent - source uninstall is best-effort */ }
                            }
                        }
                    }

                    // Store the Publish action so IwBtnCreate_Click picks it up after Intunewin finishes.
                    _pendingAfterExecuteChain = publishAction;
                    if (doIntunewinTab)
                        NavigateToIntunewinTabAndCreate(pkgFolder);
                    else if (publishAction != null)
                    {
                        // Intunewin not checked — go straight to Publish
                        _pendingAfterExecuteChain = null;
                        publishAction();
                    }
                };
            }

            // Store the chain so _onAfterCreateComplete lambda can pick it up
            _pendingAfterExecuteChain = intunewinThenPublish;

            afterCreate = _onAfterCreateComplete;
            _onAfterCreateComplete = null;

            // FireChain() runs the downstream Install/Intunewin/Publish chain. It is invoked
            // either synchronously (no source uninstall) or from the background source-uninstall's
            // completion continuation (so the chain still runs strictly AFTER the uninstall, just
            // without freezing the UI — R4).
            void FireChain()
            {
                if (sourceUninstallFailed)
                {
                    // Source uninstall failed → package folder was deleted. Do NOT run Install
                    // package / Uninstall package / Intunewin / Publish (the entire downstream
                    // chain). Abort cleanly.
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(logPath))
                            System.IO.File.AppendAllText(logPath,
                                $"[{DateTime.Now:HH:mm:ss}] Chain aborted: source uninstall failed - " +
                                $"skipping Install package and all subsequent steps.{Environment.NewLine}");
                    }
                    catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    _pendingAfterExecuteChain = null;
                    // Reflect the failure: paint the per-action status (footer + red "Uninstall source
                    // silently") and release the auto-flow lock so the Download/Package columns and top
                    // tabs reactivate. UninstallSource was set to Failed above; without this the
                    // deferred (Create Package) path aborted silently - no red checkbox, no footer.
                    AbortAutoFlow();
                }
                else if (afterCreate != null)
                {
                    // afterCreate always handles Execute + Intunewin + Publish in order
                    Dispatcher.BeginInvoke(afterCreate, System.Windows.Threading.DispatcherPriority.Normal);
                }
                else
                {
                    // Fallback — BtnCreate_Click didn't run (shouldn't happen in normal flow)
                    _pendingAfterExecuteChain = null;
                    // Do NOT reset GlobalShowMessageBox here; IwBtnCreate_Click will reset it at completion.
                    if (intunewinThenPublish != null)
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal,
                            new Action(intunewinThenPublish));
                    else
                        // No Execute / Intunewin / Publish steps queued — package creation
                        // is the terminus, so the whole auto-flow is complete here.
                        RaiseAutoFlowComplete();
                }
            }

            // If the source uninstall is running in the background, FireChain() is invoked from its
            // completion continuation. Otherwise (no source uninstall queued) fire it now.
            if (!uninstallRunningAsync)
                FireChain();
        }


        // ── IwAutoFillFromScript — delegates to AutoIntunewinPage ─────────
        public void IwAutoFillFromScript(string scriptPath)
        {
            EnsureIntunewinPageLoaded();
            _autoIntunewinPage?.IwAutoFillFromScript(scriptPath);
        }

        // True when Intunewin creation is the final step (no Publish/after chain
        // follows). Lets NotifyIntunewinCreated fire the all-complete signal.
        private bool _intunewinIsTerminus = false;

        // ── NavigateToIntunewinTabAndCreate — delegates to AutoIntunewinPage ──
        private void NavigateToIntunewinTabAndCreate(string packageFolder)
        {
            EnsureIntunewinPageLoaded();
            // Pass the pending publish action so IwBtnCreate_Click can fire it after creation.
            // In the original, IwBtnCreate_Click read _pendingAfterExecuteChain directly.
            // Now AutoIntunewinPage reads its own _pendingAfterChain field instead.
            // If nothing follows Intunewin, Intunewin creation is the terminus of the flow.
            _intunewinIsTerminus = (_pendingAfterExecuteChain == null);
            _autoIntunewinPage?.SetPendingAfterChain(_pendingAfterExecuteChain);
            _pendingAfterExecuteChain = null; // ownership transferred to AutoIntunewinPage
            _autoIntunewinPage?.NavigateToIntunewinTabAndCreate(packageFolder);
        }

    }

    // ── Bridge: gives AutoIntunewinPage access to AutoPackagePage without the class
    //    implementing IAutoPackageContext (avoids conflicts with private ActivateTopTab etc.) ──
    internal sealed class AutoIntunewinContext : IAutoPackageContext
    {
        private readonly AutoPackagePage _host;
        public AutoIntunewinContext(AutoPackagePage host) => _host = host;

        public void ActivateTopTab(string tabName)
            => _host.Dispatcher.Invoke(() => _host.ActivateTopTab(tabName));

        public bool ShowMessageBox
        {
            get => AutoShowMessageBox.Value;
            set => AutoShowMessageBox.Value = value;
        }

        public void NotifyIntunewinCreated(string intunewinPath)
        {
            // Store exact path so NavigateToPublishTab can set TxtIntunewinFile
            // directly from the creation event, bypassing the directory scan.
            _host.CaptureIntunewinPath(intunewinPath);
            _host.OnIntunewinCreated?.Invoke(intunewinPath);
        }

        public Action? ConsumePendingAfterExecuteChain()
            => _host.ConsumePendingAfterExecuteChain();

        public void NotifyAutoFlowAborted()
            => _host.Dispatcher.Invoke(() => _host.AbortAutoFlow());
    }
}
