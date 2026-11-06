using System;
using System.Windows;

namespace SwiftPack
{
    public enum AppTheme { Light, Dark }

    public static class ThemeManager
    {
        private const string LightUri = "Resources/LightTheme.xaml";
        private const string DarkUri  = "Resources/DarkTheme.xaml";

        public static AppTheme Current { get; private set; } = AppTheme.Dark;

        /// <summary>Raised after a theme is applied — pages with code-built UI
        /// subscribe to rebuild themselves with the new brushes.</summary>
        public static event Action? ThemeChanged;

        public static void Apply(AppTheme theme)
        {
            Current = theme;
            string uri = theme == AppTheme.Dark ? DarkUri : LightUri;

            var merged = Application.Current.Resources.MergedDictionaries;

            // Remove any existing theme dictionary
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                var src = merged[i].Source?.OriginalString ?? "";
                if (src.Contains("Theme.xaml"))
                {
                    merged.RemoveAt(i);
                    break;
                }
            }

            // Add the new theme
            merged.Add(new ResourceDictionary
            {
                Source = new Uri(uri, UriKind.Relative)
            });

            try { ThemeChanged?.Invoke(); } catch { }
        }

        public static void Toggle()
        {
            Apply(Current == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);
            Save();
        }

        // ── Persistence: remember the user's theme across sessions ──────
        private static string SettingsPath =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SwiftPack Studio", "settings.json");

        /// <summary>Saves the current theme choice to the per-user settings file.</summary>
        public static void Save()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(SettingsPath)!;
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(SettingsPath,
                    "{ \"Theme\": \"" + (Current == AppTheme.Dark ? "Dark" : "Light") + "\" }");
            }
            catch { /* persisting the preference must never break the app */ }
        }

        /// <summary>Applies the saved theme (Dark when no preference exists yet).</summary>
        public static void LoadSaved()
        {
            try
            {
                if (System.IO.File.Exists(SettingsPath) &&
                    System.IO.File.ReadAllText(SettingsPath).Contains("\"Light\"", StringComparison.OrdinalIgnoreCase))
                {
                    Apply(AppTheme.Light);
                    return;
                }
            }
            catch { }
            Apply(AppTheme.Dark);
        }
    }
}
