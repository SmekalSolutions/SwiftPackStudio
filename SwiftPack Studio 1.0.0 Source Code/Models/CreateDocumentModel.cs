using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SwiftPack.Models
{
    public class CreateDocumentModel : INotifyPropertyChanged
    {
        // ── Package Information ───────────────────────────────────────────────
        public string PackageName          { get; set; } = string.Empty;
        public string ApplicationName      { get; set; } = string.Empty;
        public string ApplicationVendor    { get; set; } = string.Empty;
        public string ApplicationVersion   { get; set; } = string.Empty;
        public string SourceMediaLocation  { get; set; } = string.Empty;
        public string PackageCreatedBy     { get; set; } = string.Empty;
        public string PackageValidatedBy   { get; set; } = string.Empty;
        public string CloseApplications    { get; set; } = string.Empty;
        public string Shortcuts            { get; set; } = string.Empty;
        public string InstallLogPath       { get; set; } = string.Empty;
        public string UninstallLogPath     { get; set; } = string.Empty;
        public string MsiLogPath           { get; set; } = string.Empty;
        // ── Request Details ───────────────────────────────────────────────────
        public string RequestId            { get; set; } = string.Empty;
        public string SourceLocation       { get; set; } = string.Empty;
        public string RequestorName        { get; set; } = string.Empty;
        public string ApplicationOwner     { get; set; } = string.Empty;
        public string Department           { get; set; } = string.Empty;
        public string Country              { get; set; } = string.Empty;
        // ── Deployment Configuration ──────────────────────────────────────────
        public string IntunewinFileName    { get; set; } = string.Empty;
        public string IntunewinFileSize    { get; set; } = string.Empty;
        public string IntuneName           { get; set; } = string.Empty;
        public string Publisher            { get; set; } = string.Empty;
        public string IntuneVersion        { get; set; } = string.Empty;
        public string CategoryName         { get; set; } = string.Empty;
        public string Description          { get; set; } = string.Empty;
        public string InstallCommandLine   { get; set; } = string.Empty;
        public string UninstallCommandLine { get; set; } = string.Empty;
        public string InstallBehaviour     { get; set; } = "System";
        public string OperatingSystem      { get; set; } = string.Empty;
        public string DetectionType        { get; set; } = string.Empty;
        public string Dependencies         { get; set; } = string.Empty;
        public string Supersedence         { get; set; } = string.Empty;
        public string DeploymentType       { get; set; } = string.Empty;
        public string AssignmentGroup      { get; set; } = string.Empty;
        public string PackageCompletedOn   { get; set; } = string.Empty;
        public string RollbackPlan         { get; set; } = string.Empty;
        public ObservableCollection<string> UatUsers { get; set; } = new();
        // ── Other Details ─────────────────────────────────────────────────────
        public string KnownIssues          { get; set; } = string.Empty;
        public string AdditionalInfo       { get; set; } = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
