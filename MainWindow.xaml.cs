using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PriorityPulse
{
    public sealed partial class MainWindow : Window
    {
        // ── state ──
        private readonly HashSet<string>               _targetApps    = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int>                  _handledPids   = new();
        private readonly List<(string Ts, string Msg)> _consoleLogs   = new();
        private readonly Dictionary<string, string>    _appPriorities = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int>       _appCheckTimes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime>  _lastChecked   = new(StringComparer.OrdinalIgnoreCase);
        private readonly FontFamily                    _font          = new("Segoe UI Variable Display");

        // status indicator tracking
        private readonly Dictionary<string, Microsoft.UI.Xaml.Shapes.Ellipse> _statusDots = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string>               _runningApps   = new(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer?                        _statusTimer;

        private bool   _isDarkMode      = true;
        private bool   _minimizeToTray  = true;
        private string _defaultPriority = "High";
        private int    _defaultCheckMs  = 150;
        private int    _autoClearLimit  = 0;
        private TrayIcon? _trayIcon;

        // ── constants ──
        private static readonly string[] Priorities =
            { "Low", "BelowNormal", "Normal", "AboveNormal", "High", "Realtime" };

        private static readonly (string Label, int Ms)[] CheckIntervals =
        {
            ("50ms", 50), ("100ms", 100), ("150ms", 150), ("250ms", 250), ("500ms", 500),
            ("1s", 1000), ("2s", 2000), ("5s", 5000), ("10s", 10000),
        };

        private static readonly (string Label, int Count)[] AutoClearOptions =
        {
            ("Never", 0), ("50 entries", 50), ("100 entries", 100),
            ("250 entries", 250), ("500 entries", 500), ("1000 entries", 1000),
        };

        private const string RegKeyName = "PriorityPulse";
        private const string RegPath    = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        // ── persistence paths ──
        private static readonly string DataFolder   = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PriorityPulse");
        private static readonly string AppsFile     = Path.Combine(DataFolder, "apps.json");
        private static readonly string SettingsFile = Path.Combine(DataFolder, "settings.json");

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

        // ── serialization models ──
        private class AppConfig
        {
            public string Priority { get; set; } = "High";
            public int    CheckMs  { get; set; } = 150;
        }

        private class SettingsData
        {
            public bool   IsDarkMode      { get; set; } = true;
            public bool   MinimizeToTray  { get; set; } = true;
            public string DefaultPriority { get; set; } = "High";
            public int    DefaultCheckMs  { get; set; } = 150;
            public int    AutoClearLimit  { get; set; } = 0;
        }

        private class ExportData
        {
            public Dictionary<string, AppConfig>? Apps { get; set; }
            public SettingsData? Settings { get; set; }
        }

        // ── init ──
        public MainWindow()
        {
            InitializeComponent();
            AppWindow.Resize(new Windows.Graphics.SizeInt32(980, 580));
            SystemBackdrop = new DesktopAcrylicBackdrop();

            if (AppWindow.Presenter is OverlappedPresenter p)
                p.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            LoadSettings();
            LoadAppData();
            ApplyTheme();
            InitTray();

            var hwnd = WindowNative.GetWindowHandle(this);
            if (_trayIcon != null)
            {
                SendMessage(hwnd, 0x0080, (IntPtr)1, _trayIcon.IconHandle);
                SendMessage(hwnd, 0x0080, IntPtr.Zero, _trayIcon.IconHandle);
            }

            AppNavView.SelectedItem = AppNavView.MenuItems[0];
            ShowTargetAppPage();
            StartTracker();
            StartStatusTimer();

            AppWindow.Closing += (_, e) =>
            {
                if (_minimizeToTray) { e.Cancel = true; AppWindow.Hide(); }
                else _trayIcon?.Dispose();
            };
        }

        // ── title bar controls ──
        private void TitleCloseBtn_Click(object s, RoutedEventArgs e)
        {
            if (_minimizeToTray) AppWindow.Hide();
            else { _trayIcon?.Dispose(); Application.Current.Exit(); }
        }

        private void TitleMinBtn_Click(object s, RoutedEventArgs e)
        {
            if (AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
        }

        private void TitleMaxBtn_Click(object s, RoutedEventArgs e)
        {
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                if (p.State == OverlappedPresenterState.Maximized) p.Restore();
                else p.Maximize();
            }
        }

        // ── system tray ──
        private void InitTray()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _trayIcon = new TrayIcon(hwnd, "PriorityPulse");
            _trayIcon.ShowRequested += () => { AppWindow.Show(); Activate(); };
            _trayIcon.ExitRequested += () => { _trayIcon?.Dispose(); Application.Current.Exit(); };
        }

        // ── app data persistence ──
        private void LoadAppData()
        {
            try
            {
                if (!File.Exists(AppsFile)) return;
                var data = JsonSerializer.Deserialize<Dictionary<string, AppConfig>>(File.ReadAllText(AppsFile));
                if (data == null) return;
                foreach (var (name, cfg) in data)
                {
                    _targetApps.Add(name);
                    _appPriorities[name] = cfg.Priority;
                    _appCheckTimes[name] = cfg.CheckMs;
                }
            }
            catch { }
        }

        private void SaveAppData()
        {
            try
            {
                Directory.CreateDirectory(DataFolder);
                var data = _targetApps.ToDictionary(a => a, a => new AppConfig
                {
                    Priority = _appPriorities.GetValueOrDefault(a, "High"),
                    CheckMs  = _appCheckTimes.GetValueOrDefault(a, 150)
                });
                File.WriteAllText(AppsFile, JsonSerializer.Serialize(data));
            }
            catch { }
        }

        // ── settings persistence ──
        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                var s = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(SettingsFile));
                if (s == null) return;
                _isDarkMode      = s.IsDarkMode;
                _minimizeToTray  = s.MinimizeToTray;
                _defaultPriority = s.DefaultPriority;
                _defaultCheckMs  = s.DefaultCheckMs;
                _autoClearLimit  = s.AutoClearLimit;
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(DataFolder);
                var s = new SettingsData
                {
                    IsDarkMode      = _isDarkMode,
                    MinimizeToTray  = _minimizeToTray,
                    DefaultPriority = _defaultPriority,
                    DefaultCheckMs  = _defaultCheckMs,
                    AutoClearLimit  = _autoClearLimit
                };
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(s));
            }
            catch { }
        }

        // ── export / import ──
        private async void ExportSettings_Click(object s, RoutedEventArgs e)
        {
            var picker = new FileSavePicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.SuggestedFileName = "PriorityPulse-Config";
            picker.FileTypeChoices.Add("JSON", new[] { ".json" });

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            var export = new ExportData
            {
                Apps = _targetApps.ToDictionary(a => a, a => new AppConfig
                {
                    Priority = _appPriorities.GetValueOrDefault(a, "High"),
                    CheckMs  = _appCheckTimes.GetValueOrDefault(a, 150)
                }),
                Settings = new SettingsData
                {
                    IsDarkMode      = _isDarkMode,
                    MinimizeToTray  = _minimizeToTray,
                    DefaultPriority = _defaultPriority,
                    DefaultCheckMs  = _defaultCheckMs,
                    AutoClearLimit  = _autoClearLimit
                }
            };

            var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(file.Path, json);
            AddLog("Settings exported");
        }

        private async void ImportSettings_Click(object s, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            try
            {
                var json = File.ReadAllText(file.Path);
                var import = JsonSerializer.Deserialize<ExportData>(json);
                if (import == null) return;

                // apply apps
                if (import.Apps != null)
                {
                    _targetApps.Clear();
                    _appPriorities.Clear();
                    _appCheckTimes.Clear();
                    _handledPids.Clear();
                    foreach (var (name, cfg) in import.Apps)
                    {
                        _targetApps.Add(name);
                        _appPriorities[name] = cfg.Priority;
                        _appCheckTimes[name] = cfg.CheckMs;
                    }
                    SaveAppData();
                }

                // apply settings
                if (import.Settings is SettingsData st)
                {
                    _isDarkMode      = st.IsDarkMode;
                    _minimizeToTray  = st.MinimizeToTray;
                    _defaultPriority = st.DefaultPriority;
                    _defaultCheckMs  = st.DefaultCheckMs;
                    _autoClearLimit  = st.AutoClearLimit;
                    SaveSettings();
                    ApplyTheme();
                }

                AddLog("Settings imported");
                ShowSettingsPage();
                AnimateCardsIn();
            }
            catch { AddLog("Import failed — invalid file"); }
        }

        // ── auto-start (with --minimized flag) ──
        private bool GetAutoStart()
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RegPath); return k?.GetValue(RegKeyName) != null; }
            catch { return false; }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
                if (enable)
                {
                    var exe = Environment.ProcessPath;
                    if (exe != null) k?.SetValue(RegKeyName, $"\"{exe}\" --minimized");
                }
                else k?.DeleteValue(RegKeyName, throwOnMissingValue: false);
            }
            catch { }
        }

        // ── status indicator timer ──
        private void StartStatusTimer()
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (_, _) => RefreshStatusDots();
            _statusTimer.Start();
        }

        private void RefreshStatusDots()
        {
            if (_statusDots.Count == 0) return;

            _runningApps.Clear();
            try
            {
                foreach (var proc in System.Diagnostics.Process.GetProcesses())
                {
                    string name = proc.ProcessName + ".exe";
                    if (_targetApps.Contains(name))
                        _runningApps.Add(name);
                }
            }
            catch { }

            foreach (var (app, dot) in _statusDots)
            {
                dot.Fill = new SolidColorBrush(_runningApps.Contains(app)
                    ? ColorHelper.FromArgb(255, 0, 200, 80)
                    : ColorHelper.FromArgb(60, 255, 255, 255));
            }
        }

        // ── theme ──
        private Brush CardBg     => new SolidColorBrush(_isDarkMode ? ColorHelper.FromArgb(32, 255, 255, 255) : ColorHelper.FromArgb(160, 255, 255, 255));
        private Brush CardBorder => new SolidColorBrush(_isDarkMode ? ColorHelper.FromArgb(65, 255, 255, 255) : ColorHelper.FromArgb(90, 0, 0, 0));
        private Brush TextPri    => new SolidColorBrush(_isDarkMode ? Colors.White : Colors.Black);
        private Brush TextSec    => new SolidColorBrush(_isDarkMode ? ColorHelper.FromArgb(180, 255, 255, 255) : ColorHelper.FromArgb(160, 0, 0, 0));

        private void SetBrush(ResourceDictionary res, string key, Windows.UI.Color c)
        {
            if (res.TryGetValue(key, out var v) && v is SolidColorBrush b) b.Color = c;
            else res[key] = new SolidColorBrush(c);
        }

        private void ApplyTheme()
        {
            var theme = _isDarkMode ? ElementTheme.Dark : ElementTheme.Light;
            RootGrid.RequestedTheme = theme;
            AppNavView.RequestedTheme = theme;

            RootGrid.Background = new SolidColorBrush(
                _isDarkMode ? ColorHelper.FromArgb(160, 0, 0, 0) : ColorHelper.FromArgb(100, 245, 245, 250));

            var pane = _isDarkMode ? ColorHelper.FromArgb(252, 16, 16, 16) : ColorHelper.FromArgb(252, 222, 222, 230);
            var paneDefault = _isDarkMode ? ColorHelper.FromArgb(250, 16, 16, 16) : ColorHelper.FromArgb(250, 222, 222, 230);

            AppTitleBar.Background = new SolidColorBrush(pane);

            var res = AppNavView.Resources;
            SetBrush(res, "NavigationViewDefaultPaneBackground", paneDefault);
            SetBrush(res, "NavigationViewExpandedPaneBackground", pane);
            SetBrush(res, "NavigationViewTopPaneBackground", pane);

            byte b0 = _isDarkMode ? (byte)255 : (byte)0;
            SetBrush(res, "NavigationViewItemBackgroundPointerOver",        ColorHelper.FromArgb(_isDarkMode ? (byte)18 : (byte)28, b0, b0, b0));
            SetBrush(res, "NavigationViewItemBackgroundPressed",             ColorHelper.FromArgb(_isDarkMode ? (byte)8  : (byte)42, b0, b0, b0));
            SetBrush(res, "NavigationViewItemBackgroundSelected",            ColorHelper.FromArgb(_isDarkMode ? (byte)22 : (byte)18, b0, b0, b0));
            SetBrush(res, "NavigationViewItemBackgroundSelectedPointerOver", ColorHelper.FromArgb(_isDarkMode ? (byte)28 : (byte)32, b0, b0, b0));

            AppTitleText.Foreground     = TextPri;
            AppSubtitleText.Foreground  = TextSec;
            PageTitleText.Foreground    = TextPri;
            PageSubtitleText.Foreground = TextSec;

            if (AppNavView.SelectedItem is NavigationViewItem sel)
                NavigateTo(sel.Tag?.ToString() ?? "");
        }

        // ── page transitions ──
        private async Task TransitionToPage(Action buildPage)
        {
            var eIn  = new CubicEase { EasingMode = EasingMode.EaseIn };
            var eOut = new CubicEase { EasingMode = EasingMode.EaseOut };

            var sbOut = new Storyboard();
            Anim(sbOut, PageContentHost, "Opacity", 1, 0, 80, eIn);
            Anim(sbOut, PageContentTransform, "Y", 0, 8, 80, eIn);
            Anim(sbOut, PageTitleText, "Opacity", 1, 0, 60, eIn);
            Anim(sbOut, PageSubtitleText, "Opacity", 1, 0, 60, eIn);
            sbOut.Begin();

            await Task.Delay(90);
            buildPage();

            PageContentTransform.Y = -10;
            PageTitleTransform.Y = -6;
            PageSubtitleTransform.Y = -4;

            var sbIn = new Storyboard();
            Anim(sbIn, PageTitleText, "Opacity", 0, 1, 250, eOut);
            Anim(sbIn, PageTitleTransform, "Y", -6, 0, 250, eOut);
            Anim(sbIn, PageSubtitleText, "Opacity", 0, 1, 200, eOut, 50);
            Anim(sbIn, PageSubtitleTransform, "Y", -4, 0, 200, eOut, 50);
            Anim(sbIn, PageContentHost, "Opacity", 0, 1, 280, eOut, 80);
            Anim(sbIn, PageContentTransform, "Y", -10, 0, 280, eOut, 80);
            sbIn.Begin();
        }

        // ── animation helpers ──
        private static void Anim(Storyboard sb, DependencyObject target, string prop,
            double from, double to, int ms, EasingFunctionBase ease, int delay = 0)
        {
            var a = new DoubleAnimation
            {
                From = from, To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = ease
            };
            if (delay > 0) a.BeginTime = TimeSpan.FromMilliseconds(delay);
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }

        private void AnimateCardsIn()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            int delay = 0;
            foreach (var child in PageContentHost.Children)
            {
                if (child is not Border card) continue;
                card.Opacity = 0;
                card.RenderTransform = new TranslateTransform { Y = 12 };
                var sb = new Storyboard();
                Anim(sb, card, "Opacity", 0, 1, 300, ease, delay);
                Anim(sb, card.RenderTransform, "Y", 12, 0, 300, ease, delay);
                sb.Begin();
                delay += 60;
            }
        }

        // ── navigation ──
        private void NavigateTo(string tag)
        {
            _statusDots.Clear();
            switch (tag)
            {
                case "TargetApp": ShowTargetAppPage(); break;
                case "Console":   ShowConsolePage();   break;
                case "Settings":  ShowSettingsPage();  break;
            }
            AnimateCardsIn();
        }

        private void AppNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer?.Tag?.ToString() is string tag)
                _ = TransitionToPage(() => NavigateTo(tag));
        }

        // ── target app page ──
        private async void BrowseExeButton_Click(object s, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".exe");
            var file = await picker.PickSingleFileAsync();
            if (file != null && _targetApps.Add(file.Name))
            {
                _appPriorities[file.Name] = _defaultPriority;
                _appCheckTimes[file.Name] = _defaultCheckMs;
                AddLog($"Added: {file.Name}");
                ShowTargetAppPage();
                AnimateCardsIn();
                SaveAppData();
            }
        }

        private void ShowTargetAppPage()
        {
            PageTitleText.Text    = "Target App";
            PageSubtitleText.Text = "Choose which executables to monitor.";
            PageContentHost.Children.Clear();
            _statusDots.Clear();

            var stack = MakeStack();
            stack.Children.Add(MakeTitle("Monitored Applications"));

            if (_targetApps.Count == 0)
            {
                stack.Children.Add(MakeText("No apps added yet. Click Browse to get started."));
            }
            else
            {
                foreach (var app in _targetApps)
                {
                    var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    // status dot
                    var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
                    {
                        Width = 8, Height = 8,
                        Fill = new SolidColorBrush(ColorHelper.FromArgb(60, 255, 255, 255)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0)
                    };
                    Grid.SetColumn(dot, 0);
                    row.Children.Add(dot);
                    _statusDots[app] = dot;

                    // app name
                    var label = MakeText(app);
                    label.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(label, 1);
                    row.Children.Add(label);

                    // priority selector
                    string curPri = _appPriorities.GetValueOrDefault(app, "High");
                    var priBtn = MakeDropDown(curPri, 130);
                    priBtn.Margin = new Thickness(8, 0, 8, 0);
                    priBtn.Flyout = BuildPriorityFlyout(app, priBtn);
                    Grid.SetColumn(priBtn, 2);
                    row.Children.Add(priBtn);

                    // check interval selector
                    int curMs = _appCheckTimes.GetValueOrDefault(app, 150);
                    string curLbl = CheckIntervals.FirstOrDefault(x => x.Ms == curMs).Label ?? "150ms";
                    var timeBtn = MakeDropDown(curLbl, 100);
                    timeBtn.Margin = new Thickness(0, 0, 8, 0);
                    timeBtn.Flyout = BuildFlyout(CheckIntervals.Select(x => x.Label).ToArray(), sel =>
                    {
                        _appCheckTimes[app] = CheckIntervals.First(x => x.Label == sel).Ms;
                        timeBtn.Content = sel; SaveAppData();
                    });
                    Grid.SetColumn(timeBtn, 3);
                    row.Children.Add(timeBtn);

                    // remove button
                    var rmBtn = new Button
                    {
                        Content = "\u2715", Width = 32, Height = 32,
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(_isDarkMode
                            ? ColorHelper.FromArgb(255, 255, 75, 75)
                            : ColorHelper.FromArgb(255, 200, 50, 50)),
                        FontFamily = _font
                    };
                    Grid.SetColumn(rmBtn, 4);
                    string captured = app;
                    rmBtn.Click += (_, _) =>
                    {
                        _targetApps.Remove(captured); _appPriorities.Remove(captured);
                        _appCheckTimes.Remove(captured); _lastChecked.Remove(captured);
                        AddLog($"Removed: {captured}");
                        ShowTargetAppPage(); AnimateCardsIn(); SaveAppData();
                    };
                    row.Children.Add(rmBtn);
                    stack.Children.Add(row);
                }
                stack.Children.Add(new Border { Height = 1, Background = CardBorder, Margin = new Thickness(0, 4, 0, 4) });
            }

            var browseBtn = MakeButton("Browse");
            browseBtn.Click += BrowseExeButton_Click;
            stack.Children.Add(browseBtn);
            PageContentHost.Children.Add(WrapCard(stack));

            // immediately refresh dots
            RefreshStatusDots();
        }

        // ── priority flyout with realtime warning ──
        private MenuFlyout BuildPriorityFlyout(string app, DropDownButton btn)
        {
            var flyout = new MenuFlyout();
            foreach (var pri in Priorities)
            {
                var item = new MenuFlyoutItem { Text = pri, FontFamily = _font };
                string p = pri;
                item.Click += async (_, _) =>
                {
                    if (p == "Realtime")
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "Realtime Priority",
                            Content = "Setting a process to Realtime can make your system unresponsive or freeze entirely. Only use this if you understand the risk.",
                            PrimaryButtonText = "Set Anyway",
                            CloseButtonText = "Cancel",
                            XamlRoot = PageContentHost.XamlRoot
                        };
                        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                    }
                    _appPriorities[app] = p;
                    btn.Content = p;
                    _handledPids.Clear();
                    SaveAppData();
                };
                flyout.Items.Add(item);
            }
            return flyout;
        }

        private MenuFlyout BuildDefaultPriorityFlyout(DropDownButton btn)
        {
            var flyout = new MenuFlyout();
            foreach (var pri in Priorities)
            {
                var item = new MenuFlyoutItem { Text = pri, FontFamily = _font };
                string p = pri;
                item.Click += async (_, _) =>
                {
                    if (p == "Realtime")
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "Realtime Priority",
                            Content = "Setting the default to Realtime means all new apps will be set to Realtime priority. This can make your system unresponsive.",
                            PrimaryButtonText = "Set Anyway",
                            CloseButtonText = "Cancel",
                            XamlRoot = PageContentHost.XamlRoot
                        };
                        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                    }
                    _defaultPriority = p;
                    btn.Content = p;
                    SaveSettings();
                };
                flyout.Items.Add(item);
            }
            return flyout;
        }

        // ── console page ──
        private void ShowConsolePage()
        {
            PageTitleText.Text    = "Console";
            PageSubtitleText.Text = "Live log output.";
            PageContentHost.Children.Clear();

            // log entries
            var logStack = MakeStack();
            if (_consoleLogs.Count == 0)
                logStack.Children.Add(MakeText("No activity yet."));
            else
                foreach (var (ts, msg) in _consoleLogs)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(new TextBlock
                    {
                        Text = $"[{ts}]", Opacity = 0.32, Foreground = TextPri,
                        FontFamily = _font, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
                    });
                    row.Children.Add(new TextBlock
                    {
                        Text = msg, Opacity = 0.85, Foreground = TextPri,
                        FontFamily = _font, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
                    });
                    logStack.Children.Add(row);
                }
            PageContentHost.Children.Add(WrapCard(logStack));

            // log controls
            var ctrlStack = MakeStack();
            ctrlStack.Children.Add(MakeText(
                $"{_consoleLogs.Count} log {(_consoleLogs.Count == 1 ? "entry" : "entries")} stored in memory."));
            var clearBtn = MakeButton("Clear Logs");
            clearBtn.Click += (_, _) => { _consoleLogs.Clear(); ShowConsolePage(); AnimateCardsIn(); };
            ctrlStack.Children.Add(clearBtn);
            PageContentHost.Children.Add(WrapCard(ctrlStack));
        }

        // ── settings page ──
        private void ShowSettingsPage()
        {
            PageTitleText.Text    = "Settings";
            PageSubtitleText.Text = "Adjust appearance and behavior.";
            PageContentHost.Children.Clear();

            // appearance
            var appearStack = MakeStack();
            appearStack.Children.Add(MakeTitle("Appearance"));
            var themeToggle = MakeToggle("Light Mode", !_isDarkMode);
            themeToggle.Toggled += (_, _) => { _isDarkMode = !themeToggle.IsOn; ApplyTheme(); SaveSettings(); };
            appearStack.Children.Add(themeToggle);
            PageContentHost.Children.Add(WrapCard(appearStack));

            // behavior
            var behavStack = MakeStack();
            behavStack.Children.Add(MakeTitle("Behavior"));
            var autoStart = MakeToggle("Start with Windows (minimized to tray)", GetAutoStart());
            autoStart.Toggled += (_, _) => SetAutoStart(autoStart.IsOn);
            behavStack.Children.Add(autoStart);
            var trayToggle = MakeToggle("Minimize to tray on close", _minimizeToTray);
            trayToggle.Toggled += (_, _) => { _minimizeToTray = trayToggle.IsOn; SaveSettings(); };
            behavStack.Children.Add(trayToggle);
            PageContentHost.Children.Add(WrapCard(behavStack));

            // defaults for new apps
            var defStack = MakeStack();
            defStack.Children.Add(MakeTitle("Defaults for New Apps"));
            defStack.Children.Add(MakeText("Applied automatically when you browse for an executable."));

            var defPriBtn = MakeDropDown(_defaultPriority, 130);
            defPriBtn.Flyout = BuildDefaultPriorityFlyout(defPriBtn);
            defStack.Children.Add(MakeLabeledRow("Priority", defPriBtn));

            string defTimeLbl = CheckIntervals.FirstOrDefault(x => x.Ms == _defaultCheckMs).Label ?? "150ms";
            var defTimeBtn = MakeDropDown(defTimeLbl, 100);
            defTimeBtn.Flyout = BuildFlyout(CheckIntervals.Select(x => x.Label).ToArray(), sel =>
            {
                _defaultCheckMs = CheckIntervals.First(x => x.Label == sel).Ms;
                defTimeBtn.Content = sel; SaveSettings();
            });
            defStack.Children.Add(MakeLabeledRow("Check Interval", defTimeBtn));
            PageContentHost.Children.Add(WrapCard(defStack));

            // console auto-clear
            var consStack = MakeStack();
            consStack.Children.Add(MakeTitle("Console"));
            consStack.Children.Add(MakeText(
                "Automatically clear log entries when the count exceeds the limit. \"Never\" keeps all entries until the app restarts."));

            string curClearLbl = AutoClearOptions.FirstOrDefault(x => x.Count == _autoClearLimit).Label ?? "Never";
            var clearBtn = MakeDropDown(curClearLbl, 150);
            clearBtn.Flyout = BuildFlyout(AutoClearOptions.Select(x => x.Label).ToArray(), sel =>
            {
                _autoClearLimit = AutoClearOptions.First(x => x.Label == sel).Count;
                clearBtn.Content = sel; SaveSettings();
            });
            consStack.Children.Add(MakeLabeledRow("Auto-clear after", clearBtn));
            PageContentHost.Children.Add(WrapCard(consStack));

            // export / import
            var ioStack = MakeStack();
            ioStack.Children.Add(MakeTitle("Export / Import"));
            ioStack.Children.Add(MakeText("Save or load your app list and settings as a portable config file."));

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var exportBtn = MakeButton("Export");
            exportBtn.Click += ExportSettings_Click;
            btnRow.Children.Add(exportBtn);
            var importBtn = MakeButton("Import");
            importBtn.Click += ImportSettings_Click;
            btnRow.Children.Add(importBtn);
            ioStack.Children.Add(btnRow);
            PageContentHost.Children.Add(WrapCard(ioStack));
        }

        // ── logging ──
        private void AddLog(string msg)
        {
            if (_autoClearLimit > 0 && _consoleLogs.Count >= _autoClearLimit)
                _consoleLogs.Clear();

            _consoleLogs.Add((DateTime.Now.ToString("HH:mm:ss"), msg));

            if (AppNavView.SelectedItem is NavigationViewItem { Tag: "Console" })
                ShowConsolePage();
        }

        // ── process tracker ──
        private async void StartTracker()
        {
            int cleanupTick = 0;
            await Task.Run(async () =>
            {
                while (true)
                {
                    if (_targetApps.Count > 0)
                    {
                        var now = DateTime.UtcNow;
                        var due = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var app in _targetApps)
                        {
                            int interval = _appCheckTimes.GetValueOrDefault(app, 150);
                            if (!_lastChecked.TryGetValue(app, out var last) || (now - last).TotalMilliseconds >= interval)
                            { due.Add(app); _lastChecked[app] = now; }
                        }

                        if (due.Count > 0)
                        {
                            try
                            {
                                var procs = System.Diagnostics.Process.GetProcesses();

                                if (++cleanupTick >= 100)
                                { cleanupTick = 0; _handledPids.IntersectWith(procs.Select(p => p.Id)); }

                                foreach (var proc in procs)
                                {
                                    if (_handledPids.Contains(proc.Id)) continue;
                                    string name = proc.ProcessName + ".exe";
                                    if (!due.Contains(name)) continue;

                                    string pri = _appPriorities.GetValueOrDefault(name, "High");
                                    proc.PriorityClass = pri switch
                                    {
                                        "Realtime"    => System.Diagnostics.ProcessPriorityClass.RealTime,
                                        "High"        => System.Diagnostics.ProcessPriorityClass.High,
                                        "AboveNormal" => System.Diagnostics.ProcessPriorityClass.AboveNormal,
                                        "BelowNormal" => System.Diagnostics.ProcessPriorityClass.BelowNormal,
                                        "Low"         => System.Diagnostics.ProcessPriorityClass.Idle,
                                        _             => System.Diagnostics.ProcessPriorityClass.Normal
                                    };
                                    _handledPids.Add(proc.Id);
                                    int pid = proc.Id;
                                    DispatcherQueue.TryEnqueue(() => AddLog($"Detected {name} (PID {pid}) \u2192 set to {pri}"));
                                }
                            }
                            catch { }
                        }
                    }
                    await Task.Delay(50);
                }
            });
        }

        // ── ui helpers ──
        private Border WrapCard(UIElement content) => new()
        {
            Background = CardBg, CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24), BorderBrush = CardBorder,
            BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 0, 12),
            Child = content
        };

        private StackPanel MakeStack() => new() { Spacing = 12 };

        private TextBlock MakeTitle(string t) => new()
        {
            Text = t, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPri, FontFamily = _font
        };

        private TextBlock MakeText(string t) => new()
        { Text = t, Opacity = 0.8, Foreground = TextPri, FontFamily = _font };

        private Button MakeButton(string label) => new()
        {
            Content = label, Background = CardBg, Foreground = TextPri,
            BorderBrush = CardBorder, BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(10), FontFamily = _font,
            Padding = new Thickness(16, 8, 16, 8)
        };

        private DropDownButton MakeDropDown(string label, double width) => new()
        {
            Content = label, Width = width, CornerRadius = new CornerRadius(10),
            Background = CardBg, BorderThickness = new Thickness(1.5),
            BorderBrush = CardBorder, Foreground = TextPri,
            FontFamily = _font, VerticalAlignment = VerticalAlignment.Center
        };

        private ToggleSwitch MakeToggle(string header, bool isOn) => new()
        { Header = header, IsOn = isOn, Foreground = TextPri, FontFamily = _font };

        private StackPanel MakeLabeledRow(string label, UIElement control)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(new TextBlock
            {
                Text = label, Foreground = TextPri, FontFamily = _font,
                VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85
            });
            row.Children.Add(control);
            return row;
        }

        private MenuFlyout BuildFlyout(string[] options, Action<string> onSelect)
        {
            var flyout = new MenuFlyout();
            foreach (var opt in options)
            {
                var item = new MenuFlyoutItem { Text = opt, FontFamily = _font };
                item.Click += (_, _) => onSelect(opt);
                flyout.Items.Add(item);
            }
            return flyout;
        }
    }
}
