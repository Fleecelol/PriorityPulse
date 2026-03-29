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
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PriorityPulse
{
    public sealed partial class MainWindow : Window
    {
        // ── App State ──
        private readonly HashSet<string>               _targetApps    = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int>                  _handledPids   = new();
        private readonly List<(string Ts, string Msg)> _consoleLogs   = new();
        private readonly Dictionary<string, string>    _appPriorities = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int>       _appCheckTimes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime>  _lastChecked   = new(StringComparer.OrdinalIgnoreCase);
        private readonly FontFamily                    _font          = new("Segoe UI Variable Display");

        private bool   _isDarkMode      = true;
        private bool   _minimizeToTray  = true;
        private string _defaultPriority = "High";
        private int    _defaultCheckMs  = 150;
        private TrayIcon? _trayIcon;

        // ── Constants ──
        private static readonly string[] Priorities = { "Normal", "AboveNormal", "High" };
        private static readonly (string Label, int Ms)[] CheckIntervals =
        {
            ("50ms", 50), ("100ms", 100), ("150ms", 150), ("250ms", 250), ("500ms", 500),
            ("1s", 1000), ("2s", 2000), ("5s", 5000), ("10s", 10000),
        };

        private const string RegKeyName = "PriorityPulse";
        private const string RegPath    = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

        // ── Serialization Model ──
        private class AppConfig
        {
            public string Priority { get; set; } = "High";
            public int    CheckMs  { get; set; } = 150;
        }

        // ── Initialization ──
        public MainWindow()
        {
            InitializeComponent();
            AppWindow.Resize(new Windows.Graphics.SizeInt32(980, 580));
            SystemBackdrop = new DesktopAcrylicBackdrop();

            if (AppWindow.Presenter is OverlappedPresenter p)
                p.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            ApplyTheme();
            _ = LoadAppDataAsync();
            InitTray();

            var hwnd = WindowNative.GetWindowHandle(this);
            if (_trayIcon != null)
            {
                SendMessage(hwnd, 0x0080, new IntPtr(1), _trayIcon.IconHandle);
                SendMessage(hwnd, 0x0080, new IntPtr(0), _trayIcon.IconHandle);
            }

            AppNavView.SelectedItem = AppNavView.MenuItems[0];
            ShowTargetAppPage();
            StartTracker();

            AppWindow.Closing += (_, e) =>
            {
                if (_minimizeToTray) { e.Cancel = true; AppWindow.Hide(); }
                else _trayIcon?.Dispose();
            };
        }

        // ── Title Bar Dot Buttons ──
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

        // ── System Tray ──
        private void InitTray()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _trayIcon = new TrayIcon(hwnd, "PriorityPulse");
            _trayIcon.ShowRequested += () => { AppWindow.Show(); Activate(); };
            _trayIcon.ExitRequested += () => { _trayIcon?.Dispose(); Application.Current.Exit(); };
        }

        // ── Persistence (apps.json in LocalFolder) ──
        private async Task LoadAppDataAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync("apps.json");
                var data = JsonSerializer.Deserialize<Dictionary<string, AppConfig>>(await FileIO.ReadTextAsync(file));
                if (data == null) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var (name, cfg) in data)
                    {
                        _targetApps.Add(name);
                        _appPriorities[name] = cfg.Priority;
                        _appCheckTimes[name] = cfg.CheckMs;
                    }
                    ShowTargetAppPage();
                });
            }
            catch { }
        }

        private async Task SaveAppDataAsync()
        {
            try
            {
                var data = _targetApps.ToDictionary(a => a, a => new AppConfig
                {
                    Priority = _appPriorities.GetValueOrDefault(a, "High"),
                    CheckMs  = _appCheckTimes.GetValueOrDefault(a, 150)
                });
                var file = await ApplicationData.Current.LocalFolder
                    .CreateFileAsync("apps.json", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, JsonSerializer.Serialize(data));
            }
            catch { }
        }

        // ── Auto-Start Registry ──
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
                if (enable) { var exe = Environment.ProcessPath; if (exe != null) k?.SetValue(RegKeyName, exe); }
                else k?.DeleteValue(RegKeyName, throwOnMissingValue: false);
            }
            catch { }
        }

        // ── Theme / Brushes ──
        private Brush CardBg     => new SolidColorBrush(_isDarkMode ? ColorHelper.FromArgb(28, 255, 255, 255) : ColorHelper.FromArgb(150, 255, 255, 255));
        private Brush CardBorder => new SolidColorBrush(_isDarkMode ? ColorHelper.FromArgb(55, 255, 255, 255) : ColorHelper.FromArgb(80, 0, 0, 0));
        private Brush TextPri    => new SolidColorBrush(_isDarkMode ? Colors.White : Colors.Black);
        private Brush TextSec    => new SolidColorBrush(_isDarkMode ? ColorHelper.FromArgb(180, 255, 255, 255) : ColorHelper.FromArgb(160, 0, 0, 0));

        private void SetBrush(ResourceDictionary res, string key, Windows.UI.Color color)
        {
            if (res.TryGetValue(key, out var v) && v is SolidColorBrush b) b.Color = color;
            else res[key] = new SolidColorBrush(color);
        }

        private void ApplyTheme()
        {
            var theme = _isDarkMode ? ElementTheme.Dark : ElementTheme.Light;
            RootGrid.RequestedTheme = theme;
            AppNavView.RequestedTheme = theme;

            RootGrid.Background = new SolidColorBrush(
                _isDarkMode ? ColorHelper.FromArgb(160, 0, 0, 0) : ColorHelper.FromArgb(100, 245, 245, 250));

            var paneColor = _isDarkMode
                ? ColorHelper.FromArgb(248, 16, 16, 16)
                : ColorHelper.FromArgb(248, 222, 222, 230);

            AppTitleBar.Background = new SolidColorBrush(paneColor);

            var res = AppNavView.Resources;
            SetBrush(res, "NavigationViewDefaultPaneBackground",
                _isDarkMode ? ColorHelper.FromArgb(220, 16, 16, 16) : ColorHelper.FromArgb(220, 222, 222, 230));
            SetBrush(res, "NavigationViewExpandedPaneBackground", paneColor);
            SetBrush(res, "NavigationViewTopPaneBackground", paneColor);

            byte baseA = _isDarkMode ? (byte)255 : (byte)0;
            SetBrush(res, "NavigationViewItemBackgroundPointerOver",        ColorHelper.FromArgb(_isDarkMode ? (byte)15 : (byte)25, baseA, baseA, baseA));
            SetBrush(res, "NavigationViewItemBackgroundPressed",             ColorHelper.FromArgb(_isDarkMode ? (byte)5  : (byte)40, baseA, baseA, baseA));
            SetBrush(res, "NavigationViewItemBackgroundSelected",            ColorHelper.FromArgb(_isDarkMode ? (byte)20 : (byte)15, baseA, baseA, baseA));
            SetBrush(res, "NavigationViewItemBackgroundSelectedPointerOver", ColorHelper.FromArgb(_isDarkMode ? (byte)25 : (byte)30, baseA, baseA, baseA));

            AppTitleText.Foreground    = TextPri;
            AppSubtitleText.Foreground = TextSec;
            PageTitleText.Foreground   = TextPri;
            PageSubtitleText.Foreground = TextSec;

            // Rebuild current page with new theme
            if (AppNavView.SelectedItem is NavigationViewItem sel)
                NavigateTo(sel.Tag?.ToString() ?? "");
        }

        // ── Page Transitions ──
        private async Task TransitionToPage(Action buildPage)
        {
            var easeIn  = new CubicEase { EasingMode = EasingMode.EaseIn };
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

            var sbOut = new Storyboard();
            var fadeOut  = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(100), EasingFunction = easeIn };
            var slideOut = new DoubleAnimation { From = 0, To = 6, Duration = TimeSpan.FromMilliseconds(100), EasingFunction = easeIn };
            Storyboard.SetTarget(fadeOut, PageContentHost);       Storyboard.SetTargetProperty(fadeOut, "Opacity");
            Storyboard.SetTarget(slideOut, PageContentTransform); Storyboard.SetTargetProperty(slideOut, "Y");
            sbOut.Children.Add(fadeOut); sbOut.Children.Add(slideOut);
            sbOut.Begin();

            await Task.Delay(110);
            buildPage();
            PageContentTransform.Y = -6;

            var sbIn = new Storyboard();
            var fadeIn  = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(200), EasingFunction = easeOut };
            var slideIn = new DoubleAnimation { From = -6, To = 0, Duration = TimeSpan.FromMilliseconds(200), EasingFunction = easeOut };
            Storyboard.SetTarget(fadeIn, PageContentHost);       Storyboard.SetTargetProperty(fadeIn, "Opacity");
            Storyboard.SetTarget(slideIn, PageContentTransform); Storyboard.SetTargetProperty(slideIn, "Y");
            sbIn.Children.Add(fadeIn); sbIn.Children.Add(slideIn);
            sbIn.Begin();
        }

        // ── Navigation ──
        private void NavigateTo(string tag)
        {
            switch (tag)
            {
                case "TargetApp": ShowTargetAppPage(); break;
                case "Console":   ShowConsolePage();   break;
                case "Settings":  ShowSettingsPage();  break;
            }
        }

        private void AppNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer?.Tag?.ToString() is string tag)
                _ = TransitionToPage(() => NavigateTo(tag));
        }

        // ── Target App Page ──
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
                _ = SaveAppDataAsync();
            }
        }

        private void ShowTargetAppPage()
        {
            PageTitleText.Text    = "Target App";
            PageSubtitleText.Text = "Choose which executables to monitor.";
            PageContentHost.Children.Clear();

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
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    // App name
                    var label = MakeText(app);
                    label.VerticalAlignment = VerticalAlignment.Center;
                    row.Children.Add(label);

                    // Priority dropdown
                    string curPri = _appPriorities.GetValueOrDefault(app, "High");
                    var priBtn = MakeDropDown(curPri, 130);
                    priBtn.Margin = new Thickness(8, 0, 8, 0);
                    priBtn.Flyout = BuildMenuFlyout(Priorities, sel =>
                    {
                        _appPriorities[app] = sel;
                        priBtn.Content = sel;
                        _handledPids.Clear();
                        _ = SaveAppDataAsync();
                    });
                    Grid.SetColumn(priBtn, 1);
                    row.Children.Add(priBtn);

                    // Check interval dropdown
                    int curMs = _appCheckTimes.GetValueOrDefault(app, 150);
                    string curLbl = CheckIntervals.FirstOrDefault(x => x.Ms == curMs).Label ?? "150ms";
                    var timeBtn = MakeDropDown(curLbl, 100);
                    timeBtn.Margin = new Thickness(0, 0, 8, 0);
                    timeBtn.Flyout = BuildMenuFlyout(CheckIntervals.Select(x => x.Label).ToArray(), sel =>
                    {
                        int ms = CheckIntervals.First(x => x.Label == sel).Ms;
                        _appCheckTimes[app] = ms;
                        timeBtn.Content = sel;
                        _ = SaveAppDataAsync();
                    });
                    Grid.SetColumn(timeBtn, 2);
                    row.Children.Add(timeBtn);

                    // Remove button
                    var removeBtn = new Button
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
                    Grid.SetColumn(removeBtn, 3);
                    string captured = app;
                    removeBtn.Click += (_, _) =>
                    {
                        _targetApps.Remove(captured);
                        _appPriorities.Remove(captured);
                        _appCheckTimes.Remove(captured);
                        _lastChecked.Remove(captured);
                        AddLog($"Removed: {captured}");
                        ShowTargetAppPage();
                        _ = SaveAppDataAsync();
                    };
                    row.Children.Add(removeBtn);
                    stack.Children.Add(row);
                }

                stack.Children.Add(new Border { Height = 1, Background = CardBorder, Margin = new Thickness(0, 4, 0, 4) });
            }

            var browseBtn = MakeButton("Browse");
            browseBtn.Click += BrowseExeButton_Click;
            stack.Children.Add(browseBtn);

            PageContentHost.Children.Add(WrapCard(stack));
        }

        // ── Console Page ──
        private void ShowConsolePage()
        {
            PageTitleText.Text    = "Console";
            PageSubtitleText.Text = "Live log output.";
            PageContentHost.Children.Clear();

            var stack = MakeStack();
            if (_consoleLogs.Count == 0)
                stack.Children.Add(MakeText("No activity yet."));
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
                    stack.Children.Add(row);
                }

            PageContentHost.Children.Add(WrapCard(stack));
        }

        // ── Settings Page ──
        private void ShowSettingsPage()
        {
            PageTitleText.Text    = "Settings";
            PageSubtitleText.Text = "Adjust appearance and behavior.";
            PageContentHost.Children.Clear();

            // Appearance
            var appearStack = MakeStack();
            appearStack.Children.Add(MakeTitle("Appearance"));
            var themeToggle = MakeToggle("Light Mode", !_isDarkMode);
            themeToggle.Toggled += (_, _) => { _isDarkMode = !themeToggle.IsOn; ApplyTheme(); };
            appearStack.Children.Add(themeToggle);
            PageContentHost.Children.Add(WrapCard(appearStack));

            // Behavior
            var behavStack = MakeStack();
            behavStack.Children.Add(MakeTitle("Behavior"));
            var autoStart = MakeToggle("Start with Windows", GetAutoStart());
            autoStart.Toggled += (_, _) => SetAutoStart(autoStart.IsOn);
            behavStack.Children.Add(autoStart);
            var trayToggle = MakeToggle("Minimize to tray on close", _minimizeToTray);
            trayToggle.Toggled += (_, _) => _minimizeToTray = trayToggle.IsOn;
            behavStack.Children.Add(trayToggle);
            PageContentHost.Children.Add(WrapCard(behavStack));

            // Defaults for new apps
            var defStack = MakeStack();
            defStack.Children.Add(MakeTitle("Defaults for New Apps"));
            defStack.Children.Add(MakeText("Applied automatically when you browse for an executable."));

            var defPriBtn = MakeDropDown(_defaultPriority, 130);
            defPriBtn.Flyout = BuildMenuFlyout(Priorities, sel => { _defaultPriority = sel; defPriBtn.Content = sel; });
            defStack.Children.Add(MakeLabeledRow("Priority", defPriBtn));

            string defTimeLbl = CheckIntervals.FirstOrDefault(x => x.Ms == _defaultCheckMs).Label ?? "150ms";
            var defTimeBtn = MakeDropDown(defTimeLbl, 100);
            defTimeBtn.Flyout = BuildMenuFlyout(CheckIntervals.Select(x => x.Label).ToArray(), sel =>
            {
                _defaultCheckMs = CheckIntervals.First(x => x.Label == sel).Ms;
                defTimeBtn.Content = sel;
            });
            defStack.Children.Add(MakeLabeledRow("Check Interval", defTimeBtn));
            PageContentHost.Children.Add(WrapCard(defStack));

            // Console info
            var consStack = MakeStack();
            consStack.Children.Add(MakeTitle("Console"));
            consStack.Children.Add(MakeText($"{_consoleLogs.Count} log {(_consoleLogs.Count == 1 ? "entry" : "entries")} stored in memory."));
            var clearBtn = MakeButton("Clear Logs");
            clearBtn.Click += (_, _) => { _consoleLogs.Clear(); ShowSettingsPage(); };
            consStack.Children.Add(clearBtn);
            PageContentHost.Children.Add(WrapCard(consStack));
        }

        // ── Console Logging ──
        private void AddLog(string msg)
        {
            _consoleLogs.Add((DateTime.Now.ToString("HH:mm:ss"), msg));
            if (AppNavView.SelectedItem is NavigationViewItem { Tag: "Console" })
                ShowConsolePage();
        }

        // ── Background Process Tracker ──
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
                            {
                                due.Add(app);
                                _lastChecked[app] = now;
                            }
                        }

                        if (due.Count > 0)
                        {
                            try
                            {
                                var procs = System.Diagnostics.Process.GetProcesses();

                                // Clean dead PIDs every ~5s
                                if (++cleanupTick >= 100)
                                {
                                    cleanupTick = 0;
                                    _handledPids.IntersectWith(new HashSet<int>(procs.Select(p => p.Id)));
                                }

                                foreach (var proc in procs)
                                {
                                    if (_handledPids.Contains(proc.Id)) continue;
                                    string name = proc.ProcessName + ".exe";
                                    if (!due.Contains(name)) continue;

                                    string pri = _appPriorities.GetValueOrDefault(name, "High");
                                    proc.PriorityClass = pri switch
                                    {
                                        "High"        => System.Diagnostics.ProcessPriorityClass.High,
                                        "AboveNormal" => System.Diagnostics.ProcessPriorityClass.AboveNormal,
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

        // ── UI Factory Helpers ──
        private Border WrapCard(UIElement content) => new()
        {
            Background = CardBg, CornerRadius = new CornerRadius(16),
            Padding = new Thickness(24), BorderBrush = CardBorder,
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12),
            Child = content
        };

        private StackPanel MakeStack() => new() { Spacing = 12 };

        private TextBlock MakeTitle(string t) => new()
        {
            Text = t, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPri, FontFamily = _font
        };

        private TextBlock MakeText(string t) => new()
        {
            Text = t, Opacity = 0.8, Foreground = TextPri, FontFamily = _font
        };

        private Button MakeButton(string label) => new()
        {
            Content = label, Background = CardBg, Foreground = TextPri,
            BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), FontFamily = _font,
            Padding = new Thickness(16, 8, 16, 8)
        };

        private DropDownButton MakeDropDown(string label, double width) => new()
        {
            Content = label, Width = width, CornerRadius = new CornerRadius(10),
            Background = CardBg, BorderThickness = new Thickness(1),
            BorderBrush = CardBorder, Foreground = TextPri,
            FontFamily = _font, VerticalAlignment = VerticalAlignment.Center
        };

        private ToggleSwitch MakeToggle(string header, bool isOn) => new()
        {
            Header = header, IsOn = isOn, Foreground = TextPri, FontFamily = _font
        };

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

        private MenuFlyout BuildMenuFlyout(string[] options, Action<string> onSelect)
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
