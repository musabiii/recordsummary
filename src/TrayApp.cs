using System.Diagnostics;
using Microsoft.Win32;

namespace RecordSummary;

public sealed class TrayApp : ApplicationContext, ISettingsHost
{
    enum State { Idle, CallDetected, Recording, Processing }

    static readonly Color Gray = Color.FromArgb(140, 140, 145), Red = Color.FromArgb(235, 64, 52),
        Amber = Color.FromArgb(245, 166, 35), Blue = Color.FromArgb(0, 122, 255);

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ShowSettingsEvent = @"Local\RecordSummary.ShowSettings";

    readonly EventWaitHandle _showSettings = new(false, EventResetMode.AutoReset, ShowSettingsEvent);

    Config _cfg = Config.Load();
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _statusItem, _recordItem, _autoRecordItem, _autostartItem;
    readonly CallDetector _detector;
    readonly Dictionary<Color, Icon> _icons = new();
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };
    CallRecorder? _recorder;
    string _recordingApp = "";
    bool _tiedToCall;
    Action? _balloonClick;
    string? _processingText;
    int _processing;
    SettingsWindow? _settings;

    public TrayApp()
    {
        _statusItem = new ToolStripMenuItem("Жду звонка") { Enabled = false };
        _recordItem = new ToolStripMenuItem("Начать запись", null, (_, _) => ToggleRecording());
        _autoRecordItem = new ToolStripMenuItem("Записывать звонки без вопроса") { Checked = _cfg.AutoRecord, CheckOnClick = true };
        _autoRecordItem.CheckedChanged += (_, _) => { _cfg.AutoRecord = _autoRecordItem.Checked; _cfg.Save(); };
        _autostartItem = new ToolStripMenuItem("Запускать при входе в Windows") { Checked = IsAutostart(), CheckOnClick = true };
        _autostartItem.CheckedChanged += (_, _) => SetAutostart(_autostartItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_recordItem);
        menu.Items.Add("Открыть папку записей", null, (_, _) => { Directory.CreateDirectory(_cfg.OutputPath); Shell(_cfg.OutputPath); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Настройки…", null, (_, _) => OpenSettings()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_autoRecordItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add("Открыть лог", null, (_, _) => Shell(Log.FilePath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
        _tray.BalloonTipClosed += (_, _) => _balloonClick = null;
        UpdateState();

        _statusTimer.Tick += (_, _) => UpdateState();

        _detector = new CallDetector(_cfg);
        _detector.CallStarted += OnCallStarted;
        _detector.CallEnded += OnCallEnded;

        var ui = SynchronizationContext.Current!;
        ThreadPool.RegisterWaitForSingleObject(_showSettings, (_, _) => ui.Post(_ => OpenSettings(), null), null, -1, false);
        Log.Write("Запущено");
    }

    // ---------- ISettingsHost ----------

    public Config Current => _cfg;
    public string Status => _statusItem.Text ?? "";
    public event Action? StatusChanged;

    public bool Autostart
    {
        get => IsAutostart();
        set { if (value != IsAutostart()) _autostartItem.Checked = value; }  // CheckedChanged пишет в реестр
    }

    /// <summary>Модели не держатся между звонками, так что новые настройки просто начинают действовать.</summary>
    public void Apply(Config next)
    {
        next.Save();
        _cfg = next;
        _detector.Config = next;
        _autoRecordItem.Checked = next.AutoRecord;
        Log.Write("Настройки применены");
    }

    public void Reprocess(string folder) => _ = ProcessAsync(folder, Path.GetFileName(folder));

    void OpenSettings()
    {
        if (_settings != null) { _settings.Activate(); return; }
        // WPF-окно внутри WinForms-приложения: один экземпляр Application ради темы и ресурсов.
        if (System.Windows.Application.Current == null)
            _ = new System.Windows.Application
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                ThemeMode = System.Windows.ThemeMode.System,
            };
        _settings = new SettingsWindow(this);
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_settings);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    string RecordingText =>$"Запись: {_recordingApp} {_recorder!.Elapsed:hh\\:mm\\:ss}";

    void OnCallStarted(string app)
    {
        if (_recorder != null) return;
        if (_cfg.AutoRecord)
        {
            StartRecording(app, tiedToCall: true);
            Balloon("Идёт запись", $"Звонок в {app} записывается. Запись остановится, когда звонок закончится.", null);
            return;
        }
        UpdateState();
        Balloon("Обнаружен звонок", $"{app} использует микрофон. Нажмите, чтобы записать звонок.", () =>
        {
            if (_recorder == null && _detector.ActiveCall != null) StartRecording(_detector.ActiveCall, tiedToCall: true);
        });
    }

    void OnCallEnded(string app)
    {
        if (_recorder != null && _tiedToCall) StopRecording();
        else UpdateState();
    }

    void ToggleRecording()
    {
        if (_recorder != null) StopRecording();
        else StartRecording(_detector.ActiveCall ?? "Вручную", tiedToCall: _detector.ActiveCall != null);
    }

    void StartRecording(string app, bool tiedToCall)
    {
        var folder = Path.Combine(_cfg.OutputPath, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} {SafeName(app)}");
        try { _recorder = new CallRecorder(folder); }
        catch (Exception e)
        {
            Log.Write($"Не удалось начать запись: {e}");
            Balloon("Запись не началась", e.Message, null, ToolTipIcon.Error);
            return;
        }
        _recordingApp = app;
        _tiedToCall = tiedToCall;
        _recordItem.Text = "Остановить запись";
        _statusTimer.Start();
        UpdateState();
    }

    void StopRecording()
    {
        var rec = _recorder!;
        _recorder = null;
        _statusTimer.Stop();
        rec.Dispose();
        _recordItem.Text = "Начать запись";
        _ = ProcessAsync(rec.Folder, _recordingApp);
    }

    async Task ProcessAsync(string folder, string app)
    {
        _processing++;
        // Progress захватывает UI-поток, так что статус обновляется безопасно.
        var status = new Progress<string>(s => { _processingText = s; UpdateState(); });
        try
        {
            var result = await Task.Run(() => CallProcessor.ProcessAsync(folder, _cfg, status));
            var isSummary = Path.GetFileName(result) == "summary.md";
            Balloon(isSummary ? "Саммари готово" : "Транскрипт готов", $"Звонок в {app}. Нажмите, чтобы открыть.", () => Shell(result));
        }
        catch (Exception e)
        {
            Log.Write($"Ошибка обработки {folder}: {e}");
            Balloon("Не удалось обработать звонок", $"{e.Message}\nЗапись сохранена, нажмите, чтобы открыть папку.", () => Shell(folder), ToolTipIcon.Error);
        }
        finally
        {
            if (--_processing == 0) _processingText = null;
            UpdateState();
        }
    }

    void UpdateState()
    {
        if (_recorder != null) SetState(State.Recording, RecordingText);
        else if (_processing > 0) SetState(State.Processing, _processingText ?? "Обработка…");
        else if (_detector?.ActiveCall != null) SetState(State.CallDetected, $"Звонок в {_detector.ActiveCall}");
        else SetState(State.Idle, "Жду звонка");
    }

    void Balloon(string title, string text, Action? onClick, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _balloonClick = onClick;
        _tray.ShowBalloonTip(10000, title, text, icon);
    }

    void SetState(State s, string text)
    {
        var color = s switch { State.Recording => Red, State.CallDetected => Amber, State.Processing => Blue, _ => Gray };
        _tray.Icon = IconFor(color);
        _tray.Text = ("RecordSummary: " + text)[..Math.Min(63, 15 + text.Length)];
        if (_statusItem.Text == text) return;
        _statusItem.Text = text;
        StatusChanged?.Invoke();
    }

    Icon IconFor(Color c)
    {
        if (_icons.TryGetValue(c, out var icon)) return icon;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(c);
            using var pen = new Pen(c, 3f);
            g.DrawEllipse(pen, 2, 2, 27, 27);            // кнопка записи: кольцо и точка
            g.FillEllipse(b, 9, 9, 14, 14);
        }
        icon = Icon.FromHandle(bmp.GetHicon());
        _icons[c] = icon;
        return icon;
    }

    static string SafeName(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("RecordSummary") != null;
    }

    static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("RecordSummary", $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue("RecordSummary", false);
    }

    static void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _settings?.Close();
            _recorder?.Dispose();
            _detector.Dispose();
            _statusTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            foreach (var i in _icons.Values) i.Dispose();
        }
        base.Dispose(disposing);
    }
}
