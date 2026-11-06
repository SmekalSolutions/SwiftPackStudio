using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SwiftPack.Views
{
    /// <summary>
    /// Self-contained, themed pickers for Intune apps (dependency / supersedence) and Entra
    /// groups. They read from the shared <see cref="IntuneSession"/> only, so any page can use
    /// them once the user has signed in via the top-bar "Sign in to Intune" button - no
    /// dependency on any specific page.
    /// </summary>
    public static class IntunePickerDialogs
    {
        // ── High-level entry points (sign-in check + fetch + dialog + messages) ──────────

        /// <summary>Ensures a session, loads Win32 apps, and shows the app picker. Returns the
        /// chosen apps, or null when cancelled / unavailable.</summary>
        public static async Task<List<IntuneAppItem>?> PickAppsAsync(
            Window? owner, string title, string countHeader, bool superseded, Action<string>? log = null)
        {
            if (!IntuneSession.IsConnected)
            {
                ShowMessage(owner, "Sign in required", "\u26a0", "amber",
                    "Sign in to Intune first (top right), then try again.");
                return null;
            }

            var apps = await IntuneSession.FetchAppsAsync(log);
            if (apps == null)
            {
                ShowMessage(owner, "Could not load apps", "\u26d4", "red",
                    "Could not load applications from Intune. Check the connection and try again.");
                return null;
            }
            if (apps.Count == 0)
            {
                ShowMessage(owner, "No apps", "\u26a0", "amber",
                    "No Win32 applications were found in Intune.");
                return null;
            }

            Func<IntuneAppItem, string> countOf = superseded
                ? (a => a.SupersededCount.ToString())
                : (a => a.DependentCount.ToString());
            return ShowAppPicker(owner, title, countHeader, apps, countOf);
        }

        /// <summary>Ensures a session, loads Entra groups, and shows the group picker. Returns
        /// the chosen groups, or null when cancelled / unavailable.</summary>
        public static async Task<List<EntraGroup>?> PickGroupsAsync(
            Window? owner, string title, Action<string>? log = null)
        {
            if (!IntuneSession.IsConnected)
            {
                ShowMessage(owner, "Sign in required", "\u26a0", "amber",
                    "Sign in to Intune first (top right), then try again.");
                return null;
            }

            var groups = await IntuneSession.FetchGroupsAsync(log);
            if (groups == null)
            {
                ShowMessage(owner, "Could not load groups", "\u26d4", "red",
                    "Could not load groups from Entra. Check the connection and try again.");
                return null;
            }
            if (groups.Count == 0)
            {
                ShowMessage(owner, "No groups", "\u26a0", "amber", "No groups were found in Entra.");
                return null;
            }

            return ShowGroupPicker(owner, title, groups);
        }

        // ── App picker (screenshot 1 / 2 style) ──────────────────────────────────────────
        public static List<IntuneAppItem>? ShowAppPicker(
            Window? owner, string title, string countHeader,
            List<IntuneAppItem> apps, Func<IntuneAppItem, string>? countOf = null, bool singleSelect = false)
        {
            List<IntuneAppItem>? selected = null;
            bool ok = false;
            Func<IntuneAppItem, string> countFn = countOf ?? (a => a.DependentCount.ToString());

            var winBg     = TC("NavBarBgBrush");
            var borderClr = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));
            var lblFg     = TC("TextPrimaryBrush");

            var win = new Window
            {
                Title = title,
                Width = 720, Height = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar = false,
            };
            var outer = new Border
            {
                Background = winBg, BorderBrush = borderClr, BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Color = System.Windows.Media.Colors.Black }
            };
            win.Content = outer;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // title
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // search
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // count
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons
            outer.Child = root;

            // Title bar
            var titleBarBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));
            System.Windows.Media.Brush titleTxt = System.Windows.Media.Brushes.White;
            System.Windows.Media.Brush titleSec = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            var tbar = new Border { Background = titleBarBg, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = TC("BorderMainBrush"), CornerRadius = new CornerRadius(10, 10, 0, 0), Padding = new Thickness(16, 6, 12, 6) };
            tbar.MouseLeftButtonDown += (_, _) => win.DragMove();
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ti  = new TextBlock { Text = "\U0001F680", FontSize = 14, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var tt  = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center };
            var tcl = new Button   { Content = "\u2715", FontSize = 11, Foreground = titleSec, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            tcl.Click += (_, _) => { ok = false; win.Close(); };
            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tcl, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tcl);
            tbar.Child = tg; Grid.SetRow(tbar, 0); root.Children.Add(tbar);

            // Search (placeholder overlaid and vertically centered over the textbox)
            var searchWrap = new Grid { Margin = new Thickness(16, 12, 16, 8) };
            var searchBox = new TextBox { Style = Application.Current.TryFindResource("ModernTextBoxStyle") as Style, Height = 30, FontSize = 12 };
            var hint = new TextBlock { Text = "Search by name or publisher", FontSize = 12, Foreground = TC("TextMutedBrush"), Margin = new Thickness(10, 0, 0, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            searchWrap.Children.Add(searchBox);
            searchWrap.Children.Add(hint);
            Grid.SetRow(searchWrap, 1); root.Children.Add(searchWrap);

            // Header
            void Cols(Grid gx)
            {
                gx.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
                gx.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
                gx.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                gx.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            }
            var header = new Grid { Margin = new Thickness(36, 0, 18, 4) };
            Cols(header);
            void AddH(string t, int c) { var x = new TextBlock { Text = t, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg }; Grid.SetColumn(x, c); header.Children.Add(x); }
            AddH("Name", 0); AddH("Publisher", 1); AddH("Version", 2); AddH(countHeader, 3);
            Grid.SetRow(header, 2); root.Children.Add(header);

            // List of checkboxes
            var listPanel = new StackPanel();
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16, 0, 16, 0), Content = listPanel };
            Grid.SetRow(scroll, 3); root.Children.Add(scroll);

            var countLbl = new TextBlock { FontSize = 11, Foreground = lblFg, Margin = new Thickness(20, 6, 16, 0) };
            Grid.SetRow(countLbl, 4); root.Children.Add(countLbl);

            var rows = new List<(CheckBox cb, IntuneAppItem app)>();
            void UpdateCount() { int n = rows.Count(r => r.cb.IsChecked == true); countLbl.Text = n == 0 ? "No apps selected" : (n + (n == 1 ? " app selected" : " apps selected")); }

            foreach (var app in apps)
            {
                var rg = new Grid();
                Cols(rg);
                var n = new TextBlock { Text = app.Name, FontSize = 11, Foreground = lblFg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                var p = new TextBlock { Text = app.Publisher, FontSize = 11, Foreground = lblFg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                var v = new TextBlock { Text = app.Version, FontSize = 11, Foreground = lblFg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                var cc = new TextBlock { Text = countFn(app), FontSize = 11, Foreground = lblFg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(n, 0); Grid.SetColumn(p, 1); Grid.SetColumn(v, 2); Grid.SetColumn(cc, 3);
                rg.Children.Add(n); rg.Children.Add(p); rg.Children.Add(v); rg.Children.Add(cc);
                var cb = new CheckBox { Content = rg, Margin = new Thickness(2, 5, 2, 5), Foreground = lblFg, Tag = app, VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                ApplyRoundedCheckBoxTemplate(cb);
                cb.Checked   += (_, _) => { if (singleSelect) { foreach (var rr in rows) if (!ReferenceEquals(rr.cb, cb)) rr.cb.IsChecked = false; } UpdateCount(); };
                cb.Unchecked += (_, _) => UpdateCount();
                rows.Add((cb, app));
                listPanel.Children.Add(cb);
            }
            UpdateCount();

            searchBox.TextChanged += (_, _) =>
            {
                string q = searchBox.Text.Trim().ToLowerInvariant();
                hint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                foreach (var (cb, app) in rows)
                {
                    bool match = q.Length == 0
                        || (app.Name ?? "").ToLowerInvariant().Contains(q)
                        || (app.Publisher ?? "").ToLowerInvariant().Contains(q);
                    cb.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                }
            };

            // Buttons
            var btnBar = new Grid { Margin = new Thickness(16, 8, 16, 14) };
            var bpanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var selBtn = DlgBtnStyled("Select", "blue", 110);
            var canBtn = DlgBtnStyled("Cancel", "neutral", 110); canBtn.Margin = new Thickness(8, 0, 0, 0);
            selBtn.Click += (_, _) => { selected = rows.Where(r => r.cb.IsChecked == true).Select(r => r.app).ToList(); ok = true; win.Close(); };
            canBtn.Click += (_, _) => { ok = false; win.Close(); };
            bpanel.Children.Add(selBtn); bpanel.Children.Add(canBtn);
            btnBar.Children.Add(bpanel);
            Grid.SetRow(btnBar, 5); root.Children.Add(btnBar);

            searchBox.Loaded += (_, _) => searchBox.Focus();
            win.ShowDialog();
            return ok ? selected : null;
        }

        // ── Group picker (screenshot 3 style) ────────────────────────────────────────────
        public static List<EntraGroup>? ShowGroupPicker(Window? owner, string title, List<EntraGroup> groups)
        {
            List<EntraGroup>? selected = null;
            bool ok = false;

            var winBg     = TC("NavBarBgBrush");
            var borderClr = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));
            var lblFg     = TC("TextPrimaryBrush");

            var win = new Window
            {
                Title = title,
                Width = 660, Height = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar = false,
            };
            var outer = new Border
            {
                Background = winBg, BorderBrush = borderClr, BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Color = System.Windows.Media.Colors.Black }
            };
            win.Content = outer;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = root;

            var titleBarBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));
            System.Windows.Media.Brush titleTxt = System.Windows.Media.Brushes.White;
            System.Windows.Media.Brush titleSec = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            var tbar = new Border { Background = titleBarBg, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = TC("BorderMainBrush"), CornerRadius = new CornerRadius(10, 10, 0, 0), Padding = new Thickness(16, 6, 12, 6) };
            tbar.MouseLeftButtonDown += (_, _) => win.DragMove();
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ti  = new TextBlock { Text = "\U0001F680", FontSize = 14, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var tt  = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center };
            var tcl = new Button   { Content = "\u2715", FontSize = 11, Foreground = titleSec, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            tcl.Click += (_, _) => { ok = false; win.Close(); };
            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tcl, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tcl);
            tbar.Child = tg; Grid.SetRow(tbar, 0); root.Children.Add(tbar);

            var searchWrap = new Grid { Margin = new Thickness(16, 12, 16, 8) };
            var searchBox = new TextBox { Style = Application.Current.TryFindResource("ModernTextBoxStyle") as Style, Height = 30, FontSize = 12 };
            var hint = new TextBlock { Text = "Search by name or email", FontSize = 12, Foreground = TC("TextMutedBrush"), Margin = new Thickness(10, 0, 0, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            searchWrap.Children.Add(searchBox);
            searchWrap.Children.Add(hint);
            Grid.SetRow(searchWrap, 1); root.Children.Add(searchWrap);

            void Cols(Grid gx)
            {
                gx.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
                gx.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            }
            var header = new Grid { Margin = new Thickness(36, 0, 18, 4) };
            Cols(header);
            void AddH(string t, int c) { var x = new TextBlock { Text = t, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg }; Grid.SetColumn(x, c); header.Children.Add(x); }
            AddH("Name", 0); AddH("Email", 1);
            Grid.SetRow(header, 2); root.Children.Add(header);

            var listPanel = new StackPanel();
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16, 0, 16, 0), Content = listPanel };
            Grid.SetRow(scroll, 3); root.Children.Add(scroll);

            var countLbl = new TextBlock { FontSize = 11, Foreground = lblFg, Margin = new Thickness(20, 6, 16, 0) };
            Grid.SetRow(countLbl, 4); root.Children.Add(countLbl);

            var rows = new List<(CheckBox cb, EntraGroup grp)>();
            void UpdateCount() { int n = rows.Count(r => r.cb.IsChecked == true); countLbl.Text = n == 0 ? "No groups selected" : (n + (n == 1 ? " group selected" : " groups selected")); }

            foreach (var grp in groups)
            {
                var rg = new Grid();
                Cols(rg);
                var nm = new TextBlock { Text = grp.Name, FontSize = 11, Foreground = lblFg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                var ml = new TextBlock { Text = grp.Mail, FontSize = 11, Foreground = lblFg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(nm, 0); Grid.SetColumn(ml, 1);
                rg.Children.Add(nm); rg.Children.Add(ml);
                var cb = new CheckBox { Content = rg, Margin = new Thickness(2, 5, 2, 5), Foreground = lblFg, Tag = grp, VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                ApplyRoundedCheckBoxTemplate(cb);
                cb.Checked   += (_, _) => UpdateCount();
                cb.Unchecked += (_, _) => UpdateCount();
                rows.Add((cb, grp));
                listPanel.Children.Add(cb);
            }
            UpdateCount();

            searchBox.TextChanged += (_, _) =>
            {
                string q = searchBox.Text.Trim().ToLowerInvariant();
                hint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                foreach (var (cb, grp) in rows)
                {
                    bool match = q.Length == 0
                        || (grp.Name ?? "").ToLowerInvariant().Contains(q)
                        || (grp.Mail ?? "").ToLowerInvariant().Contains(q);
                    cb.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                }
            };

            var btnBar = new Grid { Margin = new Thickness(16, 8, 16, 14) };
            var bpanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var selBtn = DlgBtnStyled("Select", "blue", 110);
            var canBtn = DlgBtnStyled("Cancel", "neutral", 110); canBtn.Margin = new Thickness(8, 0, 0, 0);
            selBtn.Click += (_, _) => { selected = rows.Where(r => r.cb.IsChecked == true).Select(r => r.grp).ToList(); ok = true; win.Close(); };
            canBtn.Click += (_, _) => { ok = false; win.Close(); };
            bpanel.Children.Add(selBtn); bpanel.Children.Add(canBtn);
            btnBar.Children.Add(bpanel);
            Grid.SetRow(btnBar, 5); root.Children.Add(btnBar);

            searchBox.Loaded += (_, _) => searchBox.Focus();
            win.ShowDialog();
            return ok ? selected : null;
        }

        // ── Small themed message dialog (single OK) ──────────────────────────────────────
        private static void ShowMessage(Window? owner, string title, string icon, string style, string message)
        {
            var winBg      = TC("WindowBgBrush");
            var borderClr  = TC("BorderMainBrush");
            var boxBg      = TC("InputBgBrush");
            var boxBdr     = TC("BorderMainBrush");
            var valFg      = TC("TextPrimaryBrush");
            var titleBarBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));

            var win = new Window
            {
                Width = 420, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None,
                AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), ShowInTaskbar = false
            };
            var outer = new Border { Background = winBg, BorderBrush = borderClr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18, Color = System.Windows.Media.Colors.Black } };
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
            var ti = new TextBlock { Text = icon, FontSize = 13, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var tt = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1);
            tg.Children.Add(ti); tg.Children.Add(tt);
            tb.Child = tg; og.Children.Add(tb);

            var msgBox = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(16, 14, 16, 6) };
            msgBox.Child = new TextBlock { Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = valFg };
            Grid.SetRow(msgBox, 1); og.Children.Add(msgBox);

            var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 2, 16, 14) };
            Grid.SetRow(btnBar, 2);
            var bOk = DlgBtnStyled("OK", "blue", 90); bOk.Margin = new Thickness(0);
            bOk.Click += (_, _) => win.Close();
            btnBar.Children.Add(bOk);
            og.Children.Add(btnBar);

            win.ShowDialog();
        }

        // ── Theme helpers (ported, self-contained) ───────────────────────────────────────
        /// <summary>
        /// Replaces the default square check glyph with a rounded-corner box
        /// (CornerRadius 4) matching the app's input styling. Built in code like the
        /// button template below; applied to every row checkbox in the pickers.
        /// </summary>
        private static void ApplyRoundedCheckBoxTemplate(CheckBox cb)
        {
            var tpl  = new ControlTemplate(typeof(CheckBox));

            var root = new FrameworkElementFactory(typeof(DockPanel));
            root.SetValue(Panel.BackgroundProperty, System.Windows.Media.Brushes.Transparent);

            var box = new FrameworkElementFactory(typeof(Border), "PART_Box");
            box.SetValue(FrameworkElement.WidthProperty,        16.0);
            box.SetValue(FrameworkElement.HeightProperty,       16.0);
            box.SetValue(Border.CornerRadiusProperty,           new CornerRadius(4));
            box.SetValue(Border.BorderThicknessProperty,        new Thickness(1.2));
            box.SetValue(Border.BorderBrushProperty,            TC("BorderMainBrush"));
            box.SetValue(Border.BackgroundProperty,             TC("InputBgBrush"));
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            box.SetValue(FrameworkElement.MarginProperty,       new Thickness(0, 0, 8, 0));
            box.SetValue(DockPanel.DockProperty,                Dock.Left);

            var tick = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path), "PART_Tick");
            tick.SetValue(System.Windows.Shapes.Path.DataProperty,
                System.Windows.Media.Geometry.Parse("M 2.5,7 L 6,10.5 L 11.5,3.5"));
            tick.SetValue(System.Windows.Shapes.Shape.StrokeProperty,          System.Windows.Media.Brushes.White);
            tick.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.8);
            tick.SetValue(System.Windows.Shapes.Shape.StrokeStartLineCapProperty, System.Windows.Media.PenLineCap.Round);
            tick.SetValue(System.Windows.Shapes.Shape.StrokeEndLineCapProperty,   System.Windows.Media.PenLineCap.Round);
            tick.SetValue(System.Windows.Shapes.Shape.StrokeLineJoinProperty,     System.Windows.Media.PenLineJoin.Round);
            tick.SetValue(UIElement.VisibilityProperty,         Visibility.Collapsed);
            tick.SetValue(FrameworkElement.MarginProperty,      new Thickness(0.5));
            box.AppendChild(tick);
            root.AppendChild(box);

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            root.AppendChild(cp);

            tpl.VisualTree = root;

            // Checked: accent fill + white tick.
            var chk = new Trigger { Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Value = true };
            chk.Setters.Add(new Setter(Border.BackgroundProperty,  TC("AccentBlueBrush"), "PART_Box"));
            chk.Setters.Add(new Setter(Border.BorderBrushProperty, TC("AccentBlueBrush"), "PART_Box"));
            chk.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible,  "PART_Tick"));
            tpl.Triggers.Add(chk);

            // Hover: accent border so the target reads as interactive.
            var hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hov.Setters.Add(new Setter(Border.BorderBrushProperty, TC("AccentBlueBrush"), "PART_Box"));
            tpl.Triggers.Add(hov);

            cb.Template = tpl;
        }

        private static System.Windows.Media.SolidColorBrush TC(string key)
        {
            var res = Application.Current.TryFindResource(key);
            return res is System.Windows.Media.SolidColorBrush b
                ? b
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
        }

        private static Button DlgBtnStyled(string text, string style, double width)
        {
            string bgKey     = style switch { "blue"  => "DlgBtnBlueBgBrush",   "green" => "DlgBtnGreenBgBrush",
                                              "red"   => "DlgBtnRedBgBrush",    "amber" => "DlgBtnAmberBgBrush",
                                              _       => "DlgBtnNeutralBgBrush" };
            string borderKey = style switch { "blue"  => "DlgBtnBlueBorderBrush",  "green" => "DlgBtnGreenBorderBrush",
                                              "red"   => "DlgBtnRedBorderBrush",   "amber" => "DlgBtnAmberBorderBrush",
                                              _       => "DlgBtnNeutralBorderBrush" };
            string fgKey     = style switch { "blue"  => "DlgBtnBlueFgBrush",   "green" => "DlgBtnGreenFgBrush",
                                              "red"   => "DlgBtnRedFgBrush",    "amber" => "DlgBtnAmberFgBrush",
                                              _       => "DlgBtnNeutralFgBrush" };
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
    }
}
