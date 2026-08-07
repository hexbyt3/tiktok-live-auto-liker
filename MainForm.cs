using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TikTokLiveAutoLiker;

sealed class MainForm : Form
{
    const int HotkeyStart = 1, HotkeyStop = 2;
    const uint VkF6 = 0x75, VkF8 = 0x77;

    const string DefaultUrl = "https://www.tiktok.com/live";

    static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TikTokLiveAutoLiker");
    static readonly string ConfigPath = Path.Combine(DataRoot, "settings.json");

    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer _ticker = new() { Interval = 250 };
    readonly System.Windows.Forms.Timer _saveDebounce = new() { Interval = 700 };

    TapEngine? _engine;
    long _startedAt;
    bool _loading;

    TextBox _url = null!;
    Button _go = null!, _start = null!, _stop = null!, _test = null!;
    NumericUpDown _intervalMin = null!, _intervalMax = null!, _maxTaps = null!;
    ComboBox _speed = null!;
    CheckBox _mute = null!, _idleBreaks = null!;
    Label _state = null!, _stats = null!;
    Button _update = null!;
    readonly UpdateService _updates = new();

    public MainForm()
    {
        Text = "TikTok Live Auto Liker";
        LoadAppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 840);
        MinimumSize = new Size(900, 620);
        Font = new Font("Segoe UI", 9f);

        BuildUi();
        LoadSettings();
        _ticker.Tick += (_, _) => UpdateStats();
        _ticker.Start();
        _ = InitWebViewAsync();

        _updates.UpdateReady += version =>
        {
            _update.Text = $"Restart for v{version}";
            _update.Visible = true;
        };
        _ = _updates.RunAsync();
    }

    /// <summary>
    /// Stops any run in progress before handing over to the updater, so a session isn't
    /// killed mid-tap by the restart.
    /// </summary>
    void ApplyUpdate()
    {
        _update.Enabled = false;
        _engine?.Stop();
        SaveSettings();
        _updates.ApplyAndRestart();
    }

    void BuildUi()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 76, Padding = new Padding(8, 6, 8, 0) };

        _url = new TextBox { Location = new Point(10, 8), Width = 640, Text = DefaultUrl };
        _go = new Button { Text = "Load", Location = new Point(658, 6), Size = new Size(62, 25) };
        _start = new Button { Text = "Start  (F6)", Location = new Point(736, 6), Size = new Size(96, 25), Enabled = false };
        _stop = new Button { Text = "Stop  (F8)", Location = new Point(838, 6), Size = new Size(88, 25), Enabled = false };
        _test = new Button { Text = "Test one", Location = new Point(932, 6), Size = new Size(80, 25), Enabled = false };

        bar.Controls.AddRange([_url, _go, _start, _stop, _test]);

        bar.Controls.Add(new Label { Text = "Speed", Location = new Point(10, 44), AutoSize = true });
        _speed = new ComboBox
        {
            Location = new Point(54, 41), Width = 84, DropDownStyle = ComboBoxStyle.DropDownList
        };
        _speed.Items.AddRange(["Relaxed", "Brisk", "Rapid"]);
        _speed.SelectedIndex = 1;
        bar.Controls.Add(_speed);

        bar.Controls.Add(new Label { Text = "Between taps", Location = new Point(150, 44), AutoSize = true });
        _intervalMin = new NumericUpDown { Location = new Point(242, 42), Width = 66, Minimum = 0, Maximum = 600000 };
        bar.Controls.Add(_intervalMin);
        bar.Controls.Add(new Label { Text = "to", Location = new Point(314, 44), AutoSize = true });
        _intervalMax = new NumericUpDown { Location = new Point(336, 42), Width = 66, Minimum = 0, Maximum = 600000 };
        bar.Controls.Add(_intervalMax);
        bar.Controls.Add(new Label { Text = "ms", Location = new Point(408, 44), AutoSize = true });

        bar.Controls.Add(new Label { Text = "Stop after", Location = new Point(448, 44), AutoSize = true });
        _maxTaps = new NumericUpDown { Location = new Point(516, 42), Width = 66, Minimum = 0, Maximum = 1000000 };
        bar.Controls.Add(_maxTaps);
        bar.Controls.Add(new Label { Text = "taps (0 = no limit)", Location = new Point(588, 44), AutoSize = true });

        _idleBreaks = new CheckBox { Text = "Occasional pauses", Location = new Point(716, 42), Size = new Size(134, 22), Checked = true };
        _mute = new CheckBox { Text = "Mute", Location = new Point(856, 42), Size = new Size(62, 22), Checked = true };
        bar.Controls.AddRange([_idleBreaks, _mute]);

        var status = new Panel { Dock = DockStyle.Bottom, Height = 26 };
        _state = new Label { Location = new Point(12, 5), AutoSize = true, Text = "Starting the browser..." };
        _stats = new Label { Location = new Point(300, 5), AutoSize = true, ForeColor = SystemColors.GrayText, Text = "0 taps" };
        _update = new Button
        {
            Text = "Restart to update",
            Size = new Size(130, 22),
            Visible = false
        };
        _update.Click += (_, _) => ApplyUpdate();
        status.Controls.AddRange([_state, _stats, _update]);

        // The panel has no real width until it is docked and laid out, so park the button
        // against the right edge on every resize rather than trusting an initial anchor.
        status.Resize += (_, _) =>
            _update.Location = new Point(status.ClientSize.Width - _update.Width - 12, 2);

        Controls.Add(_web);
        Controls.Add(bar);
        Controls.Add(status);

        _go.Click += (_, _) => Navigate();
        _url.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate(); } };
        _start.Click += (_, _) => StartTapping();
        _stop.Click += (_, _) => _engine?.Stop();
        _test.Click += (_, _) => _ = TestOneAsync();

        _mute.CheckedChanged += (_, _) => { ApplyMute(); SettingChanged(); };
        _intervalMin.ValueChanged += (_, _) => { KeepOrdered(_intervalMin, _intervalMax); SettingChanged(); };
        _intervalMax.ValueChanged += (_, _) => { KeepOrdered(_intervalMin, _intervalMax); SettingChanged(); };
        _speed.SelectedIndexChanged += (_, _) => SettingChanged();
        _maxTaps.ValueChanged += (_, _) => SettingChanged();
        _idleBreaks.CheckedChanged += (_, _) => SettingChanged();
        _url.TextChanged += (_, _) => SettingChanged();

        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveSettings(); };
    }

    /// <summary>
    /// Pushes the change straight into a running session so nothing needs restarting, and
    /// queues a save so the choice survives even if the app never gets a clean exit.
    /// </summary>
    void SettingChanged()
    {
        if (_loading) return;
        if (_engine is { Running: true } engine) engine.Apply(CurrentSettings());
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    void LoadAppIcon()
    {
        // Taken from the embedded .ico rather than the exe so the window and taskbar get the
        // full set of sizes instead of a single scaled-up 32px frame.
        using var stream = typeof(MainForm).Assembly
            .GetManifestResourceStream("TikTokLiveAutoLiker.icon.ico");
        if (stream is not null) Icon = new Icon(stream);
    }

    static void KeepOrdered(NumericUpDown min, NumericUpDown max)
    {
        if (min.Value <= max.Value) return;
        if (min.Focused) max.Value = min.Value;
        else min.Value = max.Value;
    }

    async Task InitWebViewAsync()
    {
        try
        {
            Directory.CreateDirectory(DataRoot);

            // Keeping the renderer awake matters: without these the page throttles once the
            // window is behind something else, which is exactly how this is meant to be used.
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--disable-background-timer-throttling " +
                    "--disable-renderer-backgrounding " +
                    "--disable-backgrounding-occluded-windows " +
                    "--autoplay-policy=no-user-gesture-required"
            };

            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(DataRoot, "browser"), options: options);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;

            _engine = new TapEngine(core);
            _engine.StateChanged += (_, detail) => ShowState(detail);
            _engine.Finished += () => { UpdateButtons(); UpdateStats(); };

            core.NavigationCompleted += (_, _) => ApplyMute();

            ApplyMute();
            core.Navigate(_url.Text.Trim());

            _start.Enabled = true;
            _test.Enabled = true;
            ShowState("Sign in to TikTok in this window, then press Start.");
        }
        catch (Exception ex)
        {
            ShowState("WebView2 failed to start: " + ex.Message);
            MessageBox.Show(
                "Couldn't start the embedded browser.\n\n" + ex.Message +
                "\n\nInstall the Microsoft Edge WebView2 Runtime and try again.",
                "TikTok Live Auto Liker", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ApplyMute()
    {
        if (_web.CoreWebView2 is { } core) core.IsMuted = _mute.Checked;
    }

    void Navigate()
    {
        var target = _url.Text.Trim();
        if (target.Length == 0 || _web.CoreWebView2 is not { } core) return;
        if (!target.Contains("://")) target = "https://" + target;
        _url.Text = target;
        core.Navigate(target);
    }

    TapSettings CurrentSettings() => new()
    {
        IntervalMinMs = (int)_intervalMin.Value,
        IntervalMaxMs = (int)_intervalMax.Value,
        Speed = (TapSpeed)Math.Max(0, _speed.SelectedIndex),
        IdleBreaks = _idleBreaks.Checked,
        MaxTaps = (int)_maxTaps.Value
    };

    void StartTapping()
    {
        if (_engine is null || _engine.Running) return;
        _startedAt = Environment.TickCount64;
        _engine.Start(CurrentSettings());
        UpdateButtons();
    }

    async Task TestOneAsync()
    {
        if (_engine is null || _engine.Running) return;
        _test.Enabled = false;
        try
        {
            await _engine.TapOnceAsync(CurrentSettings());
            ShowState("Sent one double-tap");
        }
        finally
        {
            _test.Enabled = true;
        }
    }

    void ShowState(string detail)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowState(detail)); return; }
        _state.Text = detail;
    }

    void UpdateButtons()
    {
        bool running = _engine?.Running == true;
        _start.Enabled = _engine is not null && !running;
        _test.Enabled = _engine is not null && !running;
        _stop.Enabled = running;
    }

    void UpdateStats()
    {
        int taps = _engine?.Taps ?? 0;
        if (taps == 0 && _engine?.Running != true)
        {
            _stats.Text = "0 taps";
            return;
        }
        double seconds = Math.Max(1, (Environment.TickCount64 - _startedAt) / 1000.0);
        var span = TimeSpan.FromSeconds(seconds);
        _stats.Text = $"{taps} taps  |  {span:hh\\:mm\\:ss}  |  {taps / (seconds / 60.0):0.0}/min";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RegisterHotKey(Handle, HotkeyStart, Native.ModNoRepeat, VkF6);
        Native.RegisterHotKey(Handle, HotkeyStop, Native.ModNoRepeat, VkF8);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WmHotkey)
        {
            if ((int)m.WParam == HotkeyStart) StartTapping();
            else if ((int)m.WParam == HotkeyStop) { _engine?.Stop(); UpdateButtons(); }
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _ticker.Stop();
        _saveDebounce.Stop();
        _updates.Dispose();
        _engine?.Stop();
        Native.UnregisterHotKey(Handle, HotkeyStart);
        Native.UnregisterHotKey(Handle, HotkeyStop);
        SaveSettings();
        base.OnFormClosing(e);
    }

    sealed record Saved(string Url, int IntervalMin, int IntervalMax, int MaxTaps, bool IdleBreaks, bool Mute, int Speed);

    void LoadSettings()
    {
        _loading = true;

        // Max first: the ordering handler would otherwise drag the min back down to meet it.
        _intervalMax.Value = 600;
        _intervalMin.Value = 250;
        _maxTaps.Value = 0;

        try
        {
            if (!File.Exists(ConfigPath)) return;
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(ConfigPath));
            if (s is null) return;

            if (!string.IsNullOrWhiteSpace(s.Url)) _url.Text = s.Url;
            _intervalMax.Value = Math.Clamp(s.IntervalMax, _intervalMax.Minimum, _intervalMax.Maximum);
            _intervalMin.Value = Math.Clamp(s.IntervalMin, _intervalMin.Minimum, _intervalMax.Value);
            _maxTaps.Value = Math.Clamp(s.MaxTaps, _maxTaps.Minimum, _maxTaps.Maximum);
            _idleBreaks.Checked = s.IdleBreaks;
            _mute.Checked = s.Mute;
            _speed.SelectedIndex = Math.Clamp(s.Speed, 0, _speed.Items.Count - 1);
        }
        catch (Exception) { }
        finally
        {
            _loading = false;
        }
    }

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            var s = new Saved(_url.Text.Trim(), (int)_intervalMin.Value, (int)_intervalMax.Value,
                (int)_maxTaps.Value, _idleBreaks.Checked, _mute.Checked, _speed.SelectedIndex);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }
}
