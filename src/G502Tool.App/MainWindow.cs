using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using G502Tool.Core;
using G502Tool.Core.Hidpp;
using G502Tool.Core.Model;

namespace G502Tool.App;

sealed class MainWindow : Window
{
    // ---- palette -----------------------------------------------------------------------
    static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x5F, 0xB8)));
    static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x5F, 0x5F, 0x5F)));
    static readonly Brush Good = Freeze(new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x0F)));
    static readonly Brush Bad = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)));
    static readonly Brush Warn = Freeze(new SolidColorBrush(Color.FromRgb(0x9D, 0x5D, 0x00)));
    static readonly Brush WarnBg = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xCE)));
    static readonly Brush Card = Freeze(new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xFB)));
    static readonly Brush Line = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)));
    static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    readonly string[] _args;
    readonly DeviceController _dev = new();
    readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };

    DeviceSnapshot? _snap;
    int _sel = -1;
    bool _busy, _loading, _connecting;
    ConnState _state = ConnState.Searching;
    string _startupText = "";

    // ---- controls ----------------------------------------------------------------------
    readonly Ellipse _dot = new() { Width = 10, Height = 10, Fill = Brushes.Gray, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _statusTitle = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _statusDetail = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 2, 0, 0) };
    readonly Button _retryBtn = new() { Content = "Retry", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(18, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    readonly StackPanel _banners = new();
    readonly StackPanel _editor = new() { IsEnabled = false, Opacity = 0.5 };

    readonly ComboBox _profileBox = new() { MinWidth = 230 };
    readonly Button _activateBtn = new() { Content = "Make active", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _profileInfo = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    readonly TextBox[] _dpi = new TextBox[ProfileSector.DpiSlots];
    readonly RadioButton[] _default = new RadioButton[ProfileSector.DpiSlots];
    readonly TextBlock[] _tag = new TextBlock[ProfileSector.DpiSlots];
    readonly TextBlock _dpiHint = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    readonly StackPanel _rateSection = new();
    readonly ComboBox _rateBox = new() { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };

    readonly StackPanel _buttonRows = new();
    readonly List<ButtonRow> _buttons = new();
    // pictures of the mouse with a numbered marker per button slot
    const double PicScale = 0.34; // picture pixels -> window pixels, the same for both views
    readonly Canvas _topMarks = new() { Width = ButtonLocations.TopSize.W, Height = ButtonLocations.TopSize.H };
    readonly Canvas _sideMarks = new() { Width = ButtonLocations.SideSize.W, Height = ButtonLocations.SideSize.H };
    readonly TextBlock _picNote = new() { Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    readonly Dictionary<int, (Border Marker, Grid Row)> _slotVisuals = new();
    int _hoverSlot = -1, _focusSlot = -1;
    static readonly Brush HiBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x00)));
    static readonly Brush RowHi = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0xE0)));
    readonly StackPanel _ledRows = new();
    readonly List<LedRow> _leds = new();
    readonly Expander _buttonsExp = new() { Header = Header("Buttons"), Margin = new Thickness(0, 14, 0, 0) };
    readonly Expander _lightExp = new() { Header = Header("Lighting"), Margin = new Thickness(0, 14, 0, 0) };

    readonly Button _saveBtn = new() { Content = "Save to mouse", Padding = new Thickness(16, 6, 16, 6), FontWeight = FontWeights.SemiBold, Background = Accent, Foreground = Brushes.White, IsEnabled = false };
    readonly Button _tempBtn = new() { Content = "Try active stage now (temporary)", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    readonly Button _revertBtn = new() { Content = "Discard edits", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    readonly TextBlock _editErrors = new() { Foreground = Bad, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _results = new() { Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock _footer = new() { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    sealed class ButtonRow
    {
        public int Index;
        public required ComboBox Box;
        public required TextBox KeyBox;
        public ButtonBinding Current = ButtonBinding.Disabled();
        public ButtonBinding? Captured;
    }

    sealed class LedRow
    {
        public int Zone;
        public required ComboBox Box;
        public required TextBox Color;
        public required Border Swatch;
        public byte[] Current = Lighting.Off();
    }

    sealed record Choice(string Label, ButtonBinding? Binding, bool Keep = false, bool Keyboard = false)
    {
        public override string ToString() => Label;
    }

    public MainWindow(string[] args)
    {
        _args = args;
        Title = "G502 HERO Config";
        Width = 600; Height = 820; MinWidth = 520; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (args.Contains(StartupRegistration.MinimizedArg)) WindowState = WindowState.Minimized; // launched at sign-in
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Background = Brushes.White;
        UseLayoutRounding = true;
        Content = BuildLayout();

        _dev.StatusChanged += s => Dispatcher.BeginInvoke(() => OnStatus(s));
        _dev.SnapshotLoaded += s => Dispatcher.BeginInvoke(() => OnSnapshot(s));
        _pollTimer.Tick += (_, _) => { if (!_dev.IsConnected) TryConnect(quiet: true); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); if (!_dev.IsConnected) TryConnect(quiet: true); };

        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        ContentRendered += OnFirstRender;
        Closing += (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true; // never cut a flash write in half
            _footer.Text = "Still talking to the mouse; the window closes when you try again in a moment.";
        };
        Closed += (_, _) => { _pollTimer.Stop(); _dev.Dispose(); };
        OnStatus(new ConnStatus(ConnState.Searching, "Looking for the G502 HERO…", ""));
    }

    // ---- startup / lifetime -------------------------------------------------------------
    void OnFirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstRender;
        var ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        _startupText = $"Window ready {ms:0} ms after launch.";
        UpdateFooter();
        int i = Array.IndexOf(_args, "--measure-startup");
        if (i >= 0)
        {
            string path = i + 1 < _args.Length ? _args[i + 1] : "startup-ms.txt";
            File.AppendAllText(path, ms.ToString("0", CultureInfo.InvariantCulture) + Environment.NewLine);
            Close();
            return;
        }
        TryConnect(quiet: false);
        _pollTimer.Start();
        Task.Run(DeviceController.IsGHubRunning).ContinueWith(t => Dispatcher.BeginInvoke(() => { _ghub = t.Result; RefreshBanners(); }), TaskScheduler.Default);
    }

    bool _ghub;

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x0219;
        if (msg == WM_DEVICECHANGE) { _debounce.Stop(); _debounce.Start(); }
        return IntPtr.Zero;
    }

    async void TryConnect(bool quiet)
    {
        if (_connecting) return;
        _connecting = true;
        try { await _dev.ConnectAsync(quiet); }
        catch (Exception ex) { OnStatus(new ConnStatus(ConnState.Error, "Unexpected error", ex.Message)); }
        finally { _connecting = false; }
    }

    // ---- start with Windows ---------------------------------------------------------------
    readonly CheckBox _autoStart = new() { Content = "Start with Windows", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };

    void InitAutoStart()
    {
        try
        {
            _syncingAutoStart = true;
            _autoStart.IsChecked = StartupRegistration.IsEnabled();
            _syncingAutoStart = false;
            var other = StartupRegistration.OtherRegisteredCommand();
            _autoStart.ToolTip = other == null
                ? "Open this app (minimized) when you sign in to Windows. Stored for your user only; untick to remove."
                : $"Currently another copy starts with Windows:\n{other}\nTick to use this copy instead.";
        }
        catch (Exception ex) { _autoStart.IsEnabled = false; _autoStart.ToolTip = $"Unavailable: {ex.Message}"; }

        // Checked/Unchecked (not Click) so keyboard, mouse and accessibility tools all apply the change.
        _autoStart.Checked += (_, _) => ApplyAutoStart(true);
        _autoStart.Unchecked += (_, _) => ApplyAutoStart(false);
    }

    bool _syncingAutoStart;

    void ApplyAutoStart(bool want)
    {
        if (_syncingAutoStart) return;
        try
        {
            StartupRegistration.Set(want);
            _footer.Text = want ? "The app will open minimized when you sign in to Windows." : "The app will no longer start with Windows.";
        }
        catch (Exception ex)
        {
            _footer.Text = $"Could not change the Windows startup setting: {ex.Message}";
        }
        _syncingAutoStart = true;
        try { _autoStart.IsChecked = StartupRegistration.IsEnabled(); } // show what is really stored
        catch { _autoStart.IsChecked = !want; }
        finally { _syncingAutoStart = false; }
    }

    // ---- layout -------------------------------------------------------------------------
    static TextBlock Header(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold };

    static Border CardOf(UIElement child, Thickness? margin = null) => new()
    {
        Child = child, Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(14, 12, 14, 12), Margin = margin ?? new Thickness(0, 12, 0, 0),
    };

    UIElement BuildLayout()
    {
        var root = new DockPanel { LastChildFill = true };

        // status
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(_dot);
        titleRow.Children.Add(_statusTitle);
        var status = new StackPanel { Margin = new Thickness(16, 14, 16, 0) };
        status.Children.Add(titleRow);
        status.Children.Add(_statusDetail);
        status.Children.Add(_retryBtn);
        status.Children.Add(_banners);
        _retryBtn.Click += (_, _) => TryConnect(quiet: false);
        DockPanel.SetDock(status, Dock.Top);
        root.Children.Add(status);

        // bottom bar
        var bar = new StackPanel { Margin = new Thickness(16, 8, 16, 12) };
        var btnRow = new WrapPanel();
        btnRow.Children.Add(_saveBtn);
        btnRow.Children.Add(_tempBtn);
        btnRow.Children.Add(_revertBtn);
        bar.Children.Add(_editErrors);
        bar.Children.Add(btnRow);
        bar.Children.Add(new TextBlock
        {
            Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = "Save to mouse writes the profile to the mouse's onboard memory, reads it back byte by byte, then checks the live values. Saved settings survive unplugging. "
                 + "Temporary changes the active DPI in the mouse's RAM only; it is lost when you switch DPI stage or profile, or unplug.",
        });
        var resultsBox = new ScrollViewer { Content = _results, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        bar.Children.Add(resultsBox);
        var footerRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var diag = new Button { Content = "Copy diagnostics", FontSize = 11, Padding = new Thickness(8, 2, 8, 2) };
        diag.Click += (_, _) => CopyDiagnostics();
        DockPanel.SetDock(diag, Dock.Right);
        footerRow.Children.Add(diag);
        DockPanel.SetDock(_autoStart, Dock.Right);
        footerRow.Children.Add(_autoStart);
        InitAutoStart();
        footerRow.Children.Add(_footer);
        bar.Children.Add(footerRow);
        var barBorder = new Border { Child = bar, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(barBorder, Dock.Bottom);
        root.Children.Add(barBorder);

        // editor
        _editor.Margin = new Thickness(16, 0, 16, 12);
        _editor.Children.Add(CardOf(BuildProfileSection()));
        _editor.Children.Add(CardOf(BuildDpiSection()));
        _editor.Children.Add(CardOf(BuildRateSection()));
        _buttonsExp.Content = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
                    Text = "Buttons are named by their factory function. The left button is locked so you cannot lose your primary click. Macros and unknown bindings are kept unless you change them." },
                BuildMousePictures(),
                _picNote,
                _buttonRows,
            },
        };
        _editor.Children.Add(_buttonsExp);
        _lightExp.Content = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
                    Text = "Lighting stored in the profile. Effects other than Off and Solid are shown and kept, but cannot be edited here." },
                _ledRows,
            },
        };
        _editor.Children.Add(_lightExp);
        root.Children.Add(new ScrollViewer { Content = _editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        _saveBtn.Click += (_, _) => Save();
        _tempBtn.Click += (_, _) => TryTemporary();
        _revertBtn.Click += (_, _) => { if (_snap != null) LoadProfile(_sel); };
        return root;
    }

    UIElement BuildProfileSection()
    {
        var p = new StackPanel();
        p.Children.Add(Header("Profile"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(_profileBox);
        row.Children.Add(_activateBtn);
        var reload = new Button { Content = "Reload from mouse", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        reload.Click += (_, _) => Reload();
        row.Children.Add(reload);
        p.Children.Add(row);
        p.Children.Add(_profileInfo);
        _profileBox.SelectionChanged += (_, _) =>
        {
            if (_loading || _snap == null || _profileBox.SelectedIndex < 0 || _profileBox.SelectedIndex == _sel) return;
            if (HasEdits() && MessageBox.Show(this, "Discard your unsaved changes to this profile?", Title, MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            {
                _loading = true; _profileBox.SelectedIndex = _sel; _loading = false;
                return;
            }
            LoadProfile(_profileBox.SelectedIndex);
        };
        _activateBtn.Click += (_, _) => ActivateSelected();
        return p;
    }

    UIElement BuildDpiSection()
    {
        var p = new StackPanel();
        p.Children.Add(Header("DPI stages"));
        var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < ProfileSector.DpiSlots; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = $"Stage {i + 1}", VerticalAlignment = VerticalAlignment.Center };
            _dpi[i] = new TextBox { Width = 84, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center };
            _dpi[i].TextChanged += (_, _) => OnEdited();
            _default[i] = new RadioButton { Content = "Default", GroupName = "defdpi", VerticalAlignment = VerticalAlignment.Center };
            _default[i].Checked += (_, _) => OnEdited();
            _tag[i] = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };
            Place(g, label, i, 0); Place(g, _dpi[i], i, 1); Place(g, _default[i], i, 2); Place(g, _tag[i], i, 3);
        }
        p.Children.Add(g);
        p.Children.Add(_dpiHint);
        return p;
    }

    static void Place(Grid g, UIElement e, int row, int col) { Grid.SetRow(e, row); Grid.SetColumn(e, col); g.Children.Add(e); }

    UIElement BuildRateSection()
    {
        _rateSection.Children.Add(Header("Polling rate"));
        _rateBox.Margin = new Thickness(0, 8, 0, 0);
        _rateBox.SelectionChanged += (_, _) => OnEdited();
        _rateSection.Children.Add(_rateBox);
        return _rateSection;
    }

    // ---- status -------------------------------------------------------------------------
    void OnStatus(ConnStatus s)
    {
        _state = s.State;
        _statusTitle.Text = s.Title;
        _statusDetail.Text = s.Detail;
        _statusDetail.Visibility = s.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _dot.Fill = s.State switch
        {
            ConnState.Connected => Good,
            ConnState.Searching => Brushes.Gray,
            ConnState.NotFound or ConnState.Disconnected => Warn,
            _ => Bad,
        };
        _retryBtn.Visibility = s.State is ConnState.Error or ConnState.NotFound or ConnState.Unsupported ? Visibility.Visible : Visibility.Collapsed;
        bool connected = s.State == ConnState.Connected;
        _editor.IsEnabled = connected;
        _editor.Opacity = connected ? 1 : 0.5;
        if (!connected)
        {
            _saveBtn.IsEnabled = _tempBtn.IsEnabled = false;
            if (s.State == ConnState.Disconnected) _snap = null;
        }
        RefreshBanners();
        UpdateButtons();
    }

    void RefreshBanners()
    {
        _banners.Children.Clear();
        if (_ghub)
            _banners.Children.Add(Banner("Logitech G HUB is running. It can overwrite these settings or switch the mouse to host mode. Quit it from its tray icon for reliable results.", null, null));
        if (_snap != null && _dev.IsConnected && _snap.HasOnboardProfiles && _snap.Mode != OnboardMode.Onboard)
            _banners.Children.Add(Banner("The mouse is in host (software) mode, usually left by G HUB. Saved settings are stored but not used until the mouse is back in onboard memory mode (switch below, or unplug and reconnect).",
                "Use onboard memory", SwitchToOnboard));
        if (_snap != null && _dev.IsConnected && _snap.HasOnboardProfiles && !_snap.DirectoryValid)
            _banners.Children.Add(Banner("The mouse is using its read-only factory profile. The first save creates editable copies of the factory profiles in onboard memory.", null, null));
    }

    Border Banner(string text, string? action, Action? onClick)
    {
        var p = new DockPanel();
        if (action != null)
        {
            var b = new Button { Content = action, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            b.Click += (_, _) => onClick!();
            DockPanel.SetDock(b, Dock.Right);
            p.Children.Add(b);
        }
        p.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Warn });
        return new Border { Child = p, Background = WarnBg, CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 8, 0, 0) };
    }

    void UpdateFooter() => _footer.Text = $"{_startupText} No account, telemetry or background service; closing this window exits the app.";

    // ---- snapshot -> UI -----------------------------------------------------------------
    void OnSnapshot(DeviceSnapshot s)
    {
        _snap = s;
        _loading = true;
        _profileBox.Items.Clear();
        foreach (var p in s.Profiles)
        {
            var tags = new List<string>();
            if (IsLiveProfile(p)) tags.Add("active");
            if (p.FromFactory) tags.Add("factory");
            else if (!p.Enabled) tags.Add("disabled");
            _profileBox.Items.Add(p.DisplayName + (tags.Count > 0 ? $"  ({string.Join(", ", tags)})" : ""));
        }
        _loading = false;
        int idx = _sel >= 0 && _sel < s.Profiles.Count ? _sel : Math.Clamp(s.ActiveProfileIndex, 0, Math.Max(0, s.Profiles.Count - 1));
        if (!s.HasOnboardProfiles) idx = -1;
        LoadProfile(idx);
        RefreshBanners();
    }

    bool IsLiveProfile(ProfileView p)
        => _snap != null && (_snap.ActiveSector == p.UserSector || (_snap.ActiveIsFactory && _snap.ActiveSector == p.SourceSector && p.FromFactory && p.Index == 0));

    void LoadProfile(int index)
    {
        var s = _snap!;
        _sel = index;
        _loading = true;
        try
        {
            _profileBox.SelectedIndex = index;
            if (index < 0)
            {
                _profileInfo.Text = "This mouse does not report onboard profiles (feature 0x8100), so nothing can be saved to it.";
                return;
            }
            var p = s.Profiles[index];
            var sec = p.Sector;
            bool active = IsLiveProfile(p);
            _profileInfo.Text = p.FromFactory
                ? "Factory profile (read-only memory). Saving creates an editable copy in onboard memory; the factory profile itself is never changed."
                : $"Stored in onboard memory · {(p.Enabled ? "enabled" : "disabled in the profile list")} · {(active ? "active on the mouse now" : "not active")}";
            _activateBtn.IsEnabled = !active && p.Enabled && !p.FromFactory;

            for (int i = 0; i < ProfileSector.DpiSlots; i++)
            {
                int v = sec.GetDpi(i);
                bool used = ProfileSector.IsStageUsed(v);
                _dpi[i].Text = used ? v.ToString(CultureInfo.InvariantCulture) : "";
                _dpi[i].IsEnabled = used;
                _dpi[i].BorderBrush = SystemColors.ControlDarkBrush;
                _default[i].IsEnabled = used;
                _default[i].IsChecked = i == sec.DefaultDpiIndex;
                var tags = new List<string>();
                if (!used) tags.Add("not used in this profile");
                if (active && s.ActiveDpiIndex == i && s.Mode == OnboardMode.Onboard) tags.Add("● current stage");
                if (used && i == sec.ShiftDpiIndex) tags.Add("sniper (DPI shift) stage");
                _tag[i].Text = string.Join(" · ", tags);
                _tag[i].Foreground = tags.Any(t => t.StartsWith("●")) ? Good : Muted;
            }
            var live = s.LiveDpi is int ld ? $" Live sensor DPI right now: {ld}." : "";
            _dpiHint.Text = (s.DpiCaps != null ? $"Allowed: {s.DpiCaps.Describe()}." : "") + live;

            _rateBox.Items.Clear();
            _rateSection.Visibility = s.SupportedIntervalsMs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var ms in s.SupportedIntervalsMs.OrderBy(x => x))
            {
                _rateBox.Items.Add(new ComboBoxItem { Content = $"{1000 / ms} Hz", Tag = ms });
                if (ms == sec.ReportIntervalMs) _rateBox.SelectedIndex = _rateBox.Items.Count - 1;
            }

            BuildButtonRows(s, sec);
            BuildLedRows(sec);
            _results.Children.Clear();
        }
        finally
        {
            _loading = false;
            OnEdited();
        }
    }

    void BuildButtonRows(DeviceSnapshot s, ProfileSector sec)
    {
        _buttonRows.Children.Clear();
        _buttons.Clear();
        _topMarks.Children.Clear();
        _sideMarks.Children.Clear();
        _slotVisuals.Clear();
        _hoverSlot = _focusSlot = -1;
        int unplaced = 0;
        for (int i = 0; i < s.ButtonCount; i++)
        {
            var cur = sec.GetButton(i);
            string factory = s.Factory != null ? KeyNames.Describe(s.Factory.GetButton(i)) : "";
            var loc = ButtonLocations.ForFactory(s.Factory?.GetButton(i));
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(4, 2, 4, 2) };
            label.Inlines.Add(new System.Windows.Documents.Run($"Button {i + 1}") { FontWeight = FontWeights.SemiBold });
            if (loc != null) label.Inlines.Add(new System.Windows.Documents.Run($"  {loc.Name}"));
            else unplaced++;
            if (factory.Length > 0)
            {
                label.Inlines.Add(new System.Windows.Documents.LineBreak());
                label.Inlines.Add(new System.Windows.Documents.Run($"factory: {factory}") { Foreground = Muted, FontSize = 11.5 });
            }
            var labelRow = new StackPanel { Orientation = Orientation.Horizontal };
            labelRow.Children.Add(SlotBadge(i, 20, 11));
            labelRow.Children.Add(label);
            Place(g, labelRow, 0, 0);
            AddSlotMarker(i, loc, g);

            if (ProfileEdit.IsPrimaryClickSlot(s, i))
            {
                Place(g, new TextBlock { Text = $"{KeyNames.Describe(cur)} (locked)", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center }, 0, 1);
                _buttonRows.Children.Add(g);
                continue;
            }

            var box = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
            var keyBox = new TextBox { Width = 200, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsReadOnly = true, Visibility = Visibility.Collapsed, Padding = new Thickness(4, 2, 4, 2) };
            var row = new ButtonRow { Index = i, Box = box, KeyBox = keyBox, Current = cur };
            box.Items.Add(new Choice($"{KeyNames.Describe(cur)} (current)", null, Keep: true));
            box.Items.Add(new Choice("Back", ButtonBinding.Mouse(4)));
            box.Items.Add(new Choice("Forward", ButtonBinding.Mouse(5)));
            box.Items.Add(new Choice("Keyboard shortcut…", null, Keyboard: true));
            box.Items.Add(new Choice("Middle click", ButtonBinding.Mouse(3)));
            box.Items.Add(new Choice("Right click", ButtonBinding.Mouse(2)));
            box.Items.Add(new Choice("Left click", ButtonBinding.Mouse(1)));
            foreach (var code in new byte[] { 0x03, 0x04, 0x05, 0x07, 0x0A, 0x08, 0x09, 0x01, 0x02 })
                box.Items.Add(new Choice(ButtonBinding.SpecialNames[code], ButtonBinding.Special(code)));
            box.Items.Add(new Choice("Disabled", ButtonBinding.Disabled()));
            if (s.Factory != null && !s.Factory.GetButton(i).Equals(cur))
                box.Items.Add(new Choice($"Factory default ({factory})", s.Factory.GetButton(i)));
            box.SelectedIndex = 0;
            if (cur.Kind == BindingKind.Keyboard) row.Captured = cur;
            box.SelectionChanged += (_, _) =>
            {
                bool kb = box.SelectedItem is Choice { Keyboard: true };
                keyBox.Visibility = kb ? Visibility.Visible : Visibility.Collapsed;
                if (kb)
                {
                    keyBox.Text = row.Captured != null ? KeyNames.Describe(row.Captured) : "Click here, then press the keys";
                    keyBox.Focus();
                }
                OnEdited();
            };
            keyBox.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                byte mods = KeyNames.ModifiersFromKeyboard();
                if (KeyNames.IsModifierKey(key)) { keyBox.Text = KeyNames.Describe(mods, 0) + "+…"; return; }
                var usage = KeyNames.HidFromKey(key);
                if (usage == null) { keyBox.Text = "That key cannot be assigned"; return; }
                row.Captured = ButtonBinding.Key(mods, usage.Value);
                keyBox.Text = KeyNames.Describe(row.Captured);
                OnEdited();
            };
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(box);
            right.Children.Add(keyBox);
            int slot = i;
            right.IsKeyboardFocusWithinChanged += (_, e) =>
            {
                if ((bool)e.NewValue) _focusSlot = slot;
                else if (_focusSlot == slot) _focusSlot = -1;
                UpdateSlotHighlight();
            };
            Place(g, right, 0, 1);
            _buttonRows.Children.Add(g);
            _buttons.Add(row);
        }
        _picNote.Text = s.Factory == null
            ? "Button positions cannot be shown: the mouse's factory profile could not be read."
            : unplaced > 0
                ? $"Point at a row to find its button on the mouse. {unplaced} button(s) have a factory function this app does not recognise, so they are not marked."
                : "Point at a row (or a number on the picture) to find that button on the mouse.";
        UpdateSlotHighlight();
    }

    // ---- button pictures ----------------------------------------------------------------
    UIElement BuildMousePictures()
    {
        static UIElement View(string file, Canvas marks, string caption)
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            using (var st = typeof(MainWindow).Assembly.GetManifestResourceStream($"Images.{file}")!)
            {
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = st;
                bmp.EndInit();
            }
            bmp.Freeze();
            var img = new System.Windows.Controls.Image
            {
                Source = bmp,
                Width = marks.Width, Height = marks.Height, Stretch = Stretch.Fill,
            };
            var layer = new Grid { Width = marks.Width, Height = marks.Height, Children = { img, marks } };
            var vb = new Viewbox { Child = layer, Width = marks.Width * PicScale, Height = marks.Height * PicScale, Stretch = Stretch.Uniform };
            var sp = new StackPanel { Margin = new Thickness(0, 0, 16, 4), VerticalAlignment = VerticalAlignment.Bottom };
            sp.Children.Add(vb);
            sp.Children.Add(new TextBlock { Text = caption, Foreground = Muted, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
            return sp;
        }
        var wrap = new WrapPanel();
        wrap.Children.Add(View("g502-top.png", _topMarks, "Top"));
        wrap.Children.Add(View("g502-side.png", _sideMarks, "Left side (thumb)"));
        return wrap;
    }

    /// <summary>Round numbered badge; also used, scaled up, as the marker on the picture.</summary>
    static Border SlotBadge(int slot, double size, double font) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
        Background = Accent, BorderBrush = Brushes.White, BorderThickness = new Thickness(size / 14),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
        Child = new TextBlock
        {
            Text = (slot + 1).ToString(CultureInfo.InvariantCulture), Foreground = Brushes.White, FontSize = font,
            FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    void AddSlotMarker(int slot, ButtonLocation? loc, Grid row)
    {
        row.MouseEnter += (_, _) => { _hoverSlot = slot; UpdateSlotHighlight(); };
        row.MouseLeave += (_, _) => { if (_hoverSlot == slot) { _hoverSlot = -1; UpdateSlotHighlight(); } };
        if (loc == null) return;
        const double size = 64; // picture pixels: about 23 px on screen
        var m = SlotBadge(slot, size, 36);
        m.Margin = new Thickness(0);
        m.Cursor = Cursors.Hand;
        m.ToolTip = $"Button {slot + 1}: {loc.Name}";
        m.RenderTransformOrigin = new Point(0.5, 0.5);
        m.MouseEnter += (_, _) => { _hoverSlot = slot; UpdateSlotHighlight(); };
        m.MouseLeave += (_, _) => { if (_hoverSlot == slot) { _hoverSlot = -1; UpdateSlotHighlight(); } };
        Canvas.SetLeft(m, loc.X - size / 2);
        Canvas.SetTop(m, loc.Y - size / 2);
        (loc.View == MouseView.Top ? _topMarks : _sideMarks).Children.Add(m);
        _slotVisuals[slot] = (m, row);
    }

    void UpdateSlotHighlight()
    {
        int hi = _hoverSlot >= 0 ? _hoverSlot : _focusSlot;
        foreach (var (slot, (marker, row)) in _slotVisuals)
        {
            bool on = slot == hi;
            marker.Background = on ? HiBrush : Accent;
            marker.Opacity = hi < 0 || on ? 1.0 : 0.45;
            marker.RenderTransform = on ? new ScaleTransform(1.3, 1.3) : Transform.Identity;
            Panel.SetZIndex(marker, on ? 1 : 0);
            row.Background = on ? RowHi : Brushes.Transparent;
        }
    }

    void BuildLedRows(ProfileSector sec)
    {
        _ledRows.Children.Clear();
        _leds.Clear();
        string[] names = { "Zone 1 (logo)", "Zone 2 (DPI indicator)" };
        for (int z = 0; z < 2; z++)
        {
            var cur = sec.GetLed(z);
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Place(g, new TextBlock { Text = names[z], VerticalAlignment = VerticalAlignment.Center }, 0, 0);
            var box = new ComboBox { MinWidth = 160 };
            box.Items.Add($"{Lighting.Describe(cur)} (current)");
            box.Items.Add("Off");
            box.Items.Add("Solid colour");
            box.SelectedIndex = 0;
            var color = new TextBox { Width = 80, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(4, 2, 4, 2), Visibility = Visibility.Collapsed, VerticalContentAlignment = VerticalAlignment.Center };
            var sw = new Border { Width = 22, Height = 22, Margin = new Thickness(6, 0, 0, 0), BorderBrush = Line, BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
            color.Text = cur[0] is Lighting.ModeSolid or Lighting.ModeBreathing ? $"#{cur[1]:X2}{cur[2]:X2}{cur[3]:X2}" : "#00AAFF";
            var row = new LedRow { Zone = z, Box = box, Color = color, Swatch = sw, Current = cur };
            box.SelectionChanged += (_, _) =>
            {
                var v = box.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
                color.Visibility = sw.Visibility = v;
                OnEdited();
            };
            color.TextChanged += (_, _) => OnEdited();
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(box); right.Children.Add(color); right.Children.Add(sw);
            Place(g, right, 0, 1);
            _ledRows.Children.Add(g);
            _leds.Add(row);
        }
    }

    // ---- edits --------------------------------------------------------------------------
    static bool TryParseColor(string t, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        t = t.Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
        r = (byte)(v >> 16); g = (byte)(v >> 8); b = (byte)v;
        return true;
    }

    ProfileEdit BuildEdit(List<string> errors)
    {
        var edit = new ProfileEdit();
        if (_snap == null || _sel < 0) return edit;
        var sec = _snap.Profiles[_sel].Sector;
        for (int i = 0; i < ProfileSector.DpiSlots; i++)
        {
            int cur = sec.GetDpi(i);
            if (!ProfileSector.IsStageUsed(cur)) continue;
            bool ok = int.TryParse(_dpi[i].Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int v)
                      && (_snap.DpiCaps == null || _snap.DpiCaps.IsValid(v));
            _dpi[i].BorderBrush = ok ? SystemColors.ControlDarkBrush : Bad;
            if (!ok) { errors.Add($"Stage {i + 1}: enter a DPI value ({_snap.DpiCaps?.Describe() ?? "100–25600"})."); continue; }
            if (v != cur) edit.Dpi[i] = v;
        }
        int def = Array.FindIndex(_default, r => r.IsChecked == true);
        if (def >= 0 && def != sec.DefaultDpiIndex) edit.DefaultDpiIndex = def;
        if (_rateBox.SelectedItem is ComboBoxItem { Tag: int ms } && ms != sec.ReportIntervalMs) edit.ReportIntervalMs = ms;
        foreach (var row in _buttons)
        {
            if (row.Box.SelectedItem is not Choice c || c.Keep) continue;
            var b = c.Keyboard ? row.Captured : c.Binding;
            if (b == null) { errors.Add($"Button {row.Index + 1}: press the keyboard shortcut to assign."); continue; }
            if (!b.Equals(row.Current)) edit.Buttons[row.Index] = b;
        }
        foreach (var led in _leds)
        {
            byte[]? want = null;
            if (led.Box.SelectedIndex == 1) want = Lighting.Off();
            else if (led.Box.SelectedIndex == 2)
            {
                if (TryParseColor(led.Color.Text, out var r, out var g, out var b))
                {
                    want = Lighting.Solid(r, g, b);
                    led.Swatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
                }
                else errors.Add($"Lighting zone {led.Zone + 1}: enter a colour like #00AAFF.");
            }
            if (want != null && !want.AsSpan().SequenceEqual(led.Current)) edit.Leds[led.Zone] = want;
        }
        return edit;
    }

    bool HasEdits() => _snap != null && _sel >= 0 && !BuildEdit(new List<string>()).IsEmpty;

    void OnEdited()
    {
        if (_loading) return;
        var errors = new List<string>();
        var edit = BuildEdit(errors);
        _editErrors.Text = string.Join(Environment.NewLine, errors);
        _editErrors.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _pendingEdit = errors.Count == 0 ? edit : null;
        _anyEdit = !edit.IsEmpty || errors.Count > 0;
        UpdateButtons();
    }

    ProfileEdit? _pendingEdit;
    bool _anyEdit;

    void UpdateButtons()
    {
        bool connected = _state == ConnState.Connected && _snap != null && _sel >= 0;
        _saveBtn.IsEnabled = connected && !_busy && _pendingEdit is { IsEmpty: false };
        _revertBtn.IsEnabled = connected && !_busy && _anyEdit;
        _tempBtn.IsEnabled = connected && !_busy && TemporaryCandidate() != null;
        _saveBtn.Content = _busy ? "Working…" : "Save to mouse";
    }

    /// <summary>The active stage's edited value, if the selected profile is live and the value differs from the sensor.</summary>
    int? TemporaryCandidate()
    {
        if (_snap == null || _sel < 0 || !_snap.Features.ContainsKey(FeatureId.AdjustableDpi)) return null;
        var p = _snap.Profiles[_sel];
        if (!IsLiveProfile(p) || _snap.ActiveDpiIndex is not int ai || ai is < 0 or >= ProfileSector.DpiSlots) return null;
        if (!int.TryParse(_dpi[ai].Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int v)) return null;
        if (_snap.DpiCaps != null && !_snap.DpiCaps.IsValid(v)) return null;
        return v != _snap.LiveDpi ? v : null;
    }

    // ---- actions ------------------------------------------------------------------------
    async Task RunOp(string busyText, Func<G502Session, OperationResult> op, bool reloadAfter)
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        _results.Children.Clear();
        _results.Children.Add(new TextBlock { Text = busyText, Foreground = Muted });
        try
        {
            var r = await _dev.RunAsync(op);
            ShowResult(r);
            if (reloadAfter)
            {
                Exception? reloadError = null;
                try { await _dev.ReloadAsync(); }
                catch (Exception e) { reloadError = e; }
                ShowResult(r); // the snapshot reload repopulates the editor and clears this panel
                if (reloadError != null)
                    _results.Children.Add(new TextBlock { Text = "Could not reload the settings afterwards: " + reloadError.Message, Foreground = Bad, TextWrapping = TextWrapping.Wrap });
            }
        }
        catch (Exception e)
        {
            ShowError(e);
            if (e is ConcurrentChangeException) await SafeReload();
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    async Task SafeReload()
    {
        try { await _dev.ReloadAsync(); }
        catch (Exception e) { ShowError(e); }
    }

    async void Save()
    {
        if (_snap == null || _sel < 0) return;
        var errors = new List<string>();
        var edit = BuildEdit(errors);
        if (errors.Count > 0 || edit.IsEmpty) return;
        var snap = _snap;
        int index = _sel;
        bool factory = snap.Profiles[index].FromFactory;
        if (factory && MessageBox.Show(this,
                "This mouse is still using its read-only factory profile.\n\nSaving will create editable copies of the factory profiles in onboard memory and apply your change to profile "
                + $"{index + 1}. The factory profile itself is not changed.\n\nContinue?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await RunOp("Writing to onboard memory and reading it back…", s => s.SaveProfile(snap, index, edit, factory), reloadAfter: true);
    }

    async void TryTemporary()
    {
        if (TemporaryCandidate() is not int v) return;
        await RunOp($"Setting {v} DPI temporarily…", s => s.ApplyTemporaryDpi(v), reloadAfter: false);
        if (_snap != null) { _snap = WithLiveDpi(_snap, v); OnEdited(); }
    }

    static DeviceSnapshot WithLiveDpi(DeviceSnapshot s, int dpi) => new()
    {
        DeviceName = s.DeviceName, Firmware = s.Firmware, ProtocolMajor = s.ProtocolMajor, ProtocolMinor = s.ProtocolMinor, Features = s.Features,
        DpiCaps = s.DpiCaps, LiveDpi = dpi, SupportedIntervalsMs = s.SupportedIntervalsMs, LiveIntervalMs = s.LiveIntervalMs, Info = s.Info, Mode = s.Mode,
        ActiveSector = s.ActiveSector, ActiveDpiIndex = s.ActiveDpiIndex, DirectoryValid = s.DirectoryValid, Profiles = s.Profiles, Factory = s.Factory,
    };

    async void ActivateSelected()
    {
        if (_snap == null || _sel < 0) return;
        if (HasEdits() && MessageBox.Show(this, "Activating reloads this profile from the mouse and discards your unsaved edits. Continue?", Title, MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        var snap = _snap; int index = _sel;
        await RunOp("Switching profile…", s => s.ActivateProfile(snap, index), reloadAfter: true);
    }

    async void SwitchToOnboard() => await RunOp("Switching to onboard memory mode…", s => s.SetOnboardMode(), reloadAfter: true);

    async void Reload()
    {
        if (HasEdits() && MessageBox.Show(this, "Discard your unsaved changes and reload from the mouse?", Title, MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        if (_busy) return;
        _busy = true; UpdateButtons();
        try { await _dev.ReloadAsync(); }
        catch (Exception e) { ShowError(e); }
        finally { _busy = false; UpdateButtons(); }
    }

    void ShowResult(OperationResult r)
    {
        _results.Children.Clear();
        bool allSkipped = r.Checks.All(c => c.Status == CheckStatus.NotChecked);
        var head = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        if (r.Success) { head.Text = $"✔ {r.Title}: done and verified on the device."; head.Foreground = Good; }
        else if (allSkipped) { head.Text = $"– {r.Title}"; head.Foreground = Muted; }
        else { head.Text = $"✖ {r.Title}: NOT verified. See below."; head.Foreground = Bad; }
        _results.Children.Add(head);
        foreach (var c in r.Checks)
        {
            var (sym, brush) = c.Status switch
            {
                CheckStatus.Verified => ("✔", Good),
                CheckStatus.Failed => ("✖", Bad),
                _ => ("–", Muted),
            };
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            t.Inlines.Add(new System.Windows.Documents.Run($"{sym} {c.What}: ") { Foreground = brush, FontWeight = FontWeights.SemiBold });
            t.Inlines.Add(new System.Windows.Documents.Run(c.Detail));
            _results.Children.Add(t);
        }
    }

    void ShowError(Exception e)
    {
        _results.Children.Clear();
        string next = e switch
        {
            DeviceDisconnectedException => "The mouse was disconnected. Plug it back in; it will be detected automatically.",
            DeviceTimeoutException => "Unplug and reconnect the mouse, then try again. Nothing is reported as saved unless it was read back.",
            HidppException => "The mouse rejected the command. Use 'Copy diagnostics' to keep the details.",
            FactoryCopyRequiredException or ConcurrentChangeException or ArgumentException => "",
            _ => "Use 'Copy diagnostics' to keep the details.",
        };
        _results.Children.Add(new TextBlock { Text = "✖ " + e.Message, Foreground = Bad, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        if (next.Length > 0) _results.Children.Add(new TextBlock { Text = next, TextWrapping = TextWrapping.Wrap });
    }

    void CopyDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"G502Tool {typeof(MainWindow).Assembly.GetName().Version} on {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")})");
        sb.AppendLine($"State: {_state} – {_statusTitle.Text}");
        sb.AppendLine(_statusDetail.Text);
        if (_dev.DevicePath != null) sb.AppendLine($"Path: {_dev.DevicePath}");
        if (_snap is { } s)
        {
            sb.AppendLine($"Device: {s.DeviceName} fw {s.Firmware} HID++ {s.ProtocolMajor}.{s.ProtocolMinor}");
            sb.AppendLine("Features: " + string.Join(" ", s.Features.Select(kv => $"{kv.Key:X4}@{kv.Value}")));
            if (s.Info is { } i) sb.AppendLine($"Onboard: {i}");
            sb.AppendLine($"Mode {s.Mode}, active sector 0x{s.ActiveSector:X4}, DPI index {s.ActiveDpiIndex}, live DPI {s.LiveDpi}, interval {s.LiveIntervalMs} ms, directory valid {s.DirectoryValid}");
            foreach (var p in s.Profiles) sb.AppendLine($"Profile {p.Index + 1} src 0x{p.SourceSector:X4} enabled={p.Enabled} factory={p.FromFactory}: {Convert.ToHexString(p.Sector.Bytes)}");
        }
        sb.AppendLine("--- HID++ trace ---");
        sb.AppendLine(_dev.Trace?.Dump() ?? "(no session)");
        try { Clipboard.SetText(sb.ToString()); _footer.Text = "Diagnostics copied to the clipboard (they stay on this PC unless you paste them somewhere)."; }
        catch (Exception e) { _footer.Text = "Could not copy: " + e.Message; }
    }
}
