using System.Diagnostics;
using Microsoft.Win32;

namespace RecordSummary;

public sealed class TrayApp : ApplicationContext, ISettingsHost, ICallsHost
{
    enum State { Idle, CallDetected, Recording, Processing }

    static readonly Color Gray = Color.FromArgb(140, 140, 145), Red = Color.FromArgb(235, 64, 52),
        Amber = Color.FromArgb(245, 166, 35), Blue = Color.FromArgb(0, 122, 255);

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Debug-сборка запускается рядом с рабочей Release, не мешая ей.
#if DEBUG
    public const string InstanceSuffix = ".Debug";
#else
    public const string InstanceSuffix = "";
#endif
    public const string ShowWindowEvent = @"Local\RecordSummary.ShowWindow" + InstanceSuffix;

    readonly EventWaitHandle _showWindow = new(false, EventResetMode.AutoReset, ShowWindowEvent);

    Config _cfg = Config.Load();
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _statusItem, _recordItem, _pauseItem, _shotItem, _videoItem, _videoSourceItem, _widgetItem, _autoRecordItem, _autostartItem;
    readonly CallDetector _detector;
    readonly Dictionary<Color, Icon> _icons = new();
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };
    CallRecorder? _recorder;
    string _recordingApp = "";
    bool _tiedToCall;
    Action? _balloonClick;
    // Папка звонка → ход его обработки. Обрабатываются по очереди, но в очереди может стоять несколько.
    readonly Dictionary<string, string> _processing = new(StringComparer.OrdinalIgnoreCase);
    string? _lastProgress;
    SettingsWindow? _settings;
    CallsWindow? _callsWindow;
    RecordingWidget? _widget;
    VideoRecorder? _video;
    VideoTarget? _videoTarget;
    VideoSourceWindow? _sourcePicker;
    bool _switchingVideo;
    bool _videoOnResume;
    bool VideoEnabled => _video?.IsRecording == true || _videoOnResume;
    readonly SynchronizationContext _ui;

    public TrayApp()
    {
        _statusItem = new ToolStripMenuItem("Жду звонка") { Enabled = false };
        _recordItem = new ToolStripMenuItem("Начать запись", null, (_, _) => ToggleRecording());
        _pauseItem = new ToolStripMenuItem("Пауза", null, (_, _) => TogglePause()) { Visible = false };
        _shotItem = new ToolStripMenuItem("Скриншот", null, (_, _) => TakeScreenshot()) { Visible = false };
        _videoItem = new ToolStripMenuItem("Записывать видео", null, (_, _) => ToggleVideo()) { Visible = false };
        _videoSourceItem = new ToolStripMenuItem("Выбрать окно для видео…", null, (_, _) => ChooseVideoSource()) { Visible = false };
        _ui = SynchronizationContext.Current!;
        _widgetItem = new ToolStripMenuItem("Показать окошко записи", null, (_, _) => ShowWidget()) { Visible = false };
        _autoRecordItem = new ToolStripMenuItem("Записывать звонки без вопроса") { Checked = _cfg.AutoRecord, CheckOnClick = true };
        _autoRecordItem.CheckedChanged += (_, _) => { _cfg.AutoRecord = _autoRecordItem.Checked; _cfg.Save(); };
        _autostartItem = new ToolStripMenuItem("Запускать при входе в Windows") { Checked = IsAutostart(), CheckOnClick = true };
        _autostartItem.CheckedChanged += (_, _) => SetAutostart(_autostartItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Записи…", null, (_, _) => OpenCalls(null)) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_recordItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_shotItem);
        menu.Items.Add(_videoItem);
        menu.Items.Add(_videoSourceItem);
        menu.Items.Add(_widgetItem);
        menu.Items.Add("Открыть папку записей", null, (_, _) => { Directory.CreateDirectory(_cfg.OutputPath); Shell(_cfg.OutputPath); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Настройки…", null, (_, _) => OpenSettings());
        menu.Items.Add(_autoRecordItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add("Открыть лог", null, (_, _) => Shell(Log.FilePath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenCalls(null);
        _tray.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
        _tray.BalloonTipClosed += (_, _) => _balloonClick = null;
        UpdateState();

        _statusTimer.Tick += (_, _) => UpdateState();

        _detector = new CallDetector(_cfg);
        _detector.CallStarted += OnCallStarted;
        _detector.CallEnded += OnCallEnded;

        var ui = SynchronizationContext.Current!;
        ThreadPool.RegisterWaitForSingleObject(_showWindow, (_, _) => ui.Post(_ => OpenCalls(null), null), null, -1, false);
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
        if (_widget != null) _widget.VideoAvailable = next.VideoMode != "off";
        UpdateMenu();
        Log.Write("Настройки применены");
    }

    public void CompressExisting()
    {
        if (_processing.ContainsKey(CompressKey)) return;
        var recording = _recorder?.Folder;
        var root = _cfg.OutputPath;
        var folders = Directory.Exists(root)
            ? Directory.GetDirectories(root).Where(f => f != recording && !_processing.ContainsKey(f)).ToList()
            : [];
        _callsWindow?.ReleasePlayer();   // плеер держит wav открытым — такой файл не удалить
        _ = CompressAsync(folders);
    }

    // Ключ сжатия в _processing: значок и статус в трее показывают его ход, как обработку звонка.
    const string CompressKey = "*сжатие*";

    async Task CompressAsync(List<string> folders)
    {
        _processing[CompressKey] = "Сжимаю записи…";
        UpdateState();
        var status = new Progress<string>(s =>
        {
            if (!_processing.ContainsKey(CompressKey)) return;
            _processing[CompressKey] = _lastProgress = s;
            UpdateState();
        });
        try
        {
            var (calls, saved) = await CallProcessor.CompressAllAsync(folders, status);
            Balloon("Записи сжаты", $"Звонков: {calls}, освобождено {saved / 1073741824.0:0.##} ГБ.", null);
        }
        catch (Exception e)
        {
            Log.Write($"Ошибка сжатия: {e}");
            Balloon("Не удалось сжать записи", e.Message, null, ToolTipIcon.Error);
        }
        finally
        {
            _processing.Remove(CompressKey);
            if (_processing.Count == 0) _lastProgress = null;
            UpdateState();
            CallsChanged?.Invoke();
        }
    }

    public void Reprocess(string folder)
    {
        if (_processing.ContainsKey(folder)) return;
        var app = System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(folder), @"^\S+ \S+ ", "");
        _ = ProcessAsync(folder, app);
    }

    public void Resummarize(string folder)
    {
        if (_processing.ContainsKey(folder)) return;
        var app = System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(folder), @"^\S+ \S+ ", "");
        _ = ProcessAsync(folder, app, summaryOnly: true);
    }

    // ---------- ICallsHost ----------

    public string? RecordingFolder => _recorder?.Folder;

    public string? ProcessingStatus(string folder) => _processing.TryGetValue(folder, out var s) ? s : null;

    public event Action? CallsChanged;

    void ICallsHost.OpenSettings() => OpenSettings();

    void OpenCalls(string? folder)
    {
        if (_callsWindow != null)
        {
            if (folder != null) _callsWindow.Select(folder);
            if (_callsWindow.WindowState == System.Windows.WindowState.Minimized) _callsWindow.WindowState = System.Windows.WindowState.Normal;
            _callsWindow.Activate();
            return;
        }
        EnsureWpf();
        _callsWindow = new CallsWindow(this, folder);
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_callsWindow);
        _callsWindow.Closed += (_, _) => _callsWindow = null;
        _callsWindow.Show();
        _callsWindow.Activate();
    }

    /// <summary>WPF-окна внутри WinForms-приложения: один экземпляр Application ради темы и ресурсов.</summary>
    static void EnsureWpf()
    {
        if (System.Windows.Application.Current == null)
            _ = new System.Windows.Application
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                ThemeMode = System.Windows.ThemeMode.System,
            };
    }

    void OpenSettings()
    {
        if (_settings != null) { _settings.Activate(); return; }
        EnsureWpf();
        _settings = new SettingsWindow(this);
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_settings);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    string RecordingText => $"{(_recorder!.IsPaused ? "Пауза" : "Запись")}: {_recordingApp} {_recorder.Elapsed:hh\\:mm\\:ss}";

    void OnCallStarted(string app)
    {
        if (_recorder != null) return;
        if (_cfg.AutoRecord)
        {
            StartRecording(app, tiedToCall: true);
            if (!_cfg.ShowRecordingWidget)
                Balloon("Идёт запись", $"Звонок в {app} записывается. Запись остановится, когда звонок закончится.", null);
            return;
        }
        UpdateState();
        if (_cfg.ShowRecordingWidget)
        {
            // Плашка не исчезает, как уведомление: висит, пока идёт звонок или пока её не закроют.
            Widget().ShowOffer(app);
            _widget!.Show();
            return;
        }
        Balloon("Обнаружен звонок", $"{app} использует микрофон. Нажмите, чтобы записать звонок.", RecordActiveCall);
    }

    void RecordActiveCall()
    {
        if (_recorder == null && _detector.ActiveCall != null) StartRecording(_detector.ActiveCall, tiedToCall: true);
    }

    void OnCallEnded(string app)
    {
        if (_recorder != null && _tiedToCall) StopRecording();
        else
        {
            if (_recorder == null) CloseWidget();   // предложение записать звонок, который уже закончился
            UpdateState();
        }
    }

    // ---------- Плашка записи ----------

    RecordingWidget Widget()
    {
        if (_widget != null) return _widget;
        EnsureWpf();
        _widget = new RecordingWidget(_cfg.WidgetLeft, _cfg.WidgetTop);
        _widget.RecordRequested += RecordActiveCall;
        _widget.PauseToggled += TogglePause;
        _widget.ScreenshotRequested += TakeScreenshot;
        _widget.VideoToggled += ToggleVideo;
        _widget.VideoSourceRequested += ChooseVideoSource;
        _widget.StopRequested += () => { if (_recorder != null) StopRecording(); };
        _widget.Dismissed += () =>
        {
            CloseWidget();
            UpdateMenu();   // во время записи — вернуть плашку можно из меню трея
        };
        _widget.Moved += (left, top) =>
        {
            _cfg.WidgetLeft = left;
            _cfg.WidgetTop = top;
            _cfg.Save();
        };
        _widget.Closed += (_, _) => _widget = null;
        return _widget;
    }

    void ShowWidget()
    {
        if (_recorder == null) return;
        var widget = Widget();
        widget.VideoAvailable = _cfg.VideoMode != "off";
        widget.ShowRecording(_recordingApp, _recorder);
        widget.SetVideo(VideoEnabled, _videoTarget?.Title ?? _video?.Source);
        widget.Show();
        UpdateMenu();
    }

    // ---------- Видео ----------

    void ToggleVideo()
    {
        if (_recorder == null || _video == null || _switchingVideo) return;
        if (VideoEnabled) { _videoOnResume = false; _video.Stop(); }
        else StartVideo();
        _widget?.SetVideo(VideoEnabled, _videoTarget?.Title ?? _video.Source);
        UpdateMenu();
    }

    void StartVideo()
    {
        // На паузе сохраняем выбор и включаем захват только при продолжении звонка.
        if (_recorder?.IsPaused == true) { _videoOnResume = true; return; }
        _videoOnResume = false;
        try
        {
            _video!.Start(_cfg, _detector.ActiveCallKey, _videoTarget);
        }
        catch (Exception e)
        {
            Log.Write($"Видео не началось: {e}");
            Balloon("Видео не записывается", e.Message, null, ToolTipIcon.Error);
        }
    }

    async void ChooseVideoSource()
    {
        if (_switchingVideo) return;
        if (_sourcePicker != null) { _sourcePicker.Activate(); return; }
        if (_recorder == null || _video == null || _switchingVideo) return;
        var video = _video;
        var recorder = _recorder;
        try
        {
            EnsureWpf();
            var picker = _sourcePicker = new VideoSourceWindow(_videoTarget ?? video.Target);
            if (picker.ShowDialog() != true || picker.SelectedTarget is not { } target) return;
            if (_video != video || _recorder != recorder) return;
            var current = _videoTarget ?? video.Target;
            if (current?.Window == target.Window && current.Display == target.Display) return;

            _switchingVideo = true;
            UpdateMenu();
            VideoRecorder.ValidateTarget(target);
            bool resume = VideoEnabled;
            _videoOnResume = false;
            await video.StopAndWaitAsync();
            if (_video != video || _recorder != recorder) return;
            _videoTarget = target;
            if (resume) StartVideo();
            _widget?.SetVideo(VideoEnabled, target.Title);
        }
        catch (Exception e)
        {
            Log.Write($"Не удалось сменить источник видео: {e}");
            _widget?.SetVideo(VideoEnabled, _videoTarget?.Title ?? _video?.Source);
            Balloon("Источник видео не изменён", e.Message, null, ToolTipIcon.Error);
        }
        finally
        {
            _sourcePicker = null;
            _switchingVideo = false;
            UpdateMenu();
        }
    }

    /// <summary>Запись видео упала посреди звонка (например, закрыли окно): звук пишется дальше, видео — выключаем.</summary>
    void OnVideoFailed(VideoRecorder video, string error) => _ui.Post(_ =>
    {
        if (video != _video || video.IsRecording) return;
        _videoOnResume = false;
        _widget?.SetVideo(false);
        UpdateMenu();
        Balloon("Видео остановилось", $"{error}\nЗвук записывается дальше — видео можно включить снова.", null, ToolTipIcon.Warning);
    }, null);

    void CloseWidget() => _widget?.Close();

    void TogglePause()
    {
        if (_recorder == null) return;
        // Видео на паузе вместе со звуком: оба идут по одним часам записи.
        if (_recorder.IsPaused)
        {
            _recorder.Resume();
            if (_videoOnResume) StartVideo();
            else _video?.Resume();
        }
        else { _recorder.Pause(); _video?.Pause(); }
        _widget?.SetVideo(VideoEnabled, _videoTarget?.Title ?? _video?.Source);
        UpdateMenu();
        UpdateState();
    }

    void TakeScreenshot()
    {
        if (_recorder == null) return;
        try
        {
            Screenshots.Capture(_recorder.Folder, _recorder.Elapsed);
            int count = Screenshots.Count(_recorder.Folder);
            if (_widget != null) _widget.ShowShotSaved(count);
            else Balloon("Скриншот сохранён", $"Скриншот {count} в записи звонка.", null);
        }
        catch (Exception e)
        {
            Log.Write($"Скриншот не сохранён: {e}");
            Balloon("Скриншот не сохранён", e.Message, null, ToolTipIcon.Error);
        }
    }

    /// <summary>Пункты меню, которые зависят от записи: пауза, скриншот и "Показать окошко".</summary>
    void UpdateMenu()
    {
        bool recording = _recorder != null;
        _recordItem.Text = recording ? "Остановить запись" : "Начать запись";
        _pauseItem.Visible = _shotItem.Visible = recording;
        _pauseItem.Text = _recorder?.IsPaused == true ? "Продолжить запись" : "Пауза";
        _videoItem.Visible = recording && _cfg.VideoMode != "off";
        _videoItem.Checked = VideoEnabled;
        _videoSourceItem.Visible = _videoItem.Visible;
        _videoSourceItem.Enabled = _videoItem.Enabled = !_switchingVideo;
        _videoSourceItem.ToolTipText = _videoTarget?.Title ?? _video?.Source ?? "";
        _widgetItem.Visible = recording && _widget == null;
    }

    void ToggleRecording()
    {
        if (_recorder != null) StopRecording();
        else StartRecording(_detector.ActiveCall ?? "Вручную", tiedToCall: _detector.ActiveCall != null);
    }

    void StartRecording(string app, bool tiedToCall)
    {
        var folder = Path.Combine(_cfg.OutputPath, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} {SafeName(app)}");
        try { _recorder = new CallRecorder(folder, CallAppPatterns(app)); }
        catch (Exception e)
        {
            Log.Write($"Не удалось начать запись: {e}");
            Balloon("Запись не началась", e.Message, null, ToolTipIcon.Error);
            return;
        }
        _recordingApp = app;
        _videoTarget = null;
        _videoOnResume = false;
        _tiedToCall = tiedToCall;
        var rec = _recorder;
        var video = _video = new VideoRecorder(folder, () => rec.Elapsed);
        video.Failed += error => OnVideoFailed(video, error);
        if (_cfg.VideoMode == "always") StartVideo();
        _statusTimer.Start();
        if (_cfg.ShowRecordingWidget) ShowWidget();
        else CloseWidget();
        UpdateMenu();
        UpdateState();
        CallsChanged?.Invoke();
    }

    /// <summary>Фрагменты пути к exe этого приложения; при записи вручную — всех приложений для звонков.</summary>
    string[] CallAppPatterns(string app)
    {
        var own = _cfg.CallApps.Where(p => p.Value == app).Select(p => p.Key).ToArray();
        return own.Length > 0 ? own : _cfg.CallApps.Keys.ToArray();
    }

    void StopRecording()
    {
        var rec = _recorder!;
        // Видео — первым: конец фрагмента берётся по часам записи, пока они ещё идут.
        _video?.Stop();
        _video = null;
        _videoOnResume = false;
        _recorder = null;
        _sourcePicker?.Close();
        _statusTimer.Stop();
        rec.Dispose();
        CloseWidget();
        UpdateMenu();
        _ = ProcessAsync(rec.Folder, _recordingApp);
    }

    async Task ProcessAsync(string folder, string app, bool summaryOnly = false)
    {
        _processing[folder] = summaryOnly ? "В очереди на саммари" : "В очереди на обработку";
        CallsChanged?.Invoke();
        // Progress захватывает UI-поток, так что статус обновляется безопасно.
        var status = new Progress<string>(s =>
        {
            if (!_processing.ContainsKey(folder)) return;   // запоздавшее сообщение после окончания
            _processing[folder] = _lastProgress = s;
            UpdateState();
        });
        try
        {
            var result = await Task.Run(() => CallProcessor.ProcessAsync(folder, _cfg, status, summaryOnly));
            var isSummary = Path.GetFileName(result) == "summary.md";
            Balloon(isSummary ? "Саммари готово" : "Транскрипт готов", $"Звонок в {app}. Нажмите, чтобы открыть.", () => OpenCalls(folder));
        }
        catch (Exception e)
        {
            Log.Write($"Ошибка обработки {folder}: {e}");
            Balloon("Не удалось обработать звонок", $"{e.Message}\nЗапись сохранена, нажмите, чтобы открыть.", () => OpenCalls(folder), ToolTipIcon.Error);
        }
        finally
        {
            _processing.Remove(folder);
            if (_processing.Count == 0) _lastProgress = null;
            UpdateState();
            CallsChanged?.Invoke();
        }
    }

    void UpdateState()
    {
        if (_recorder != null) SetState(State.Recording, RecordingText);
        else if (_processing.Count > 0) SetState(State.Processing, _lastProgress ?? "Обработка…");
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
            _callsWindow?.Close();
            _widget?.Close();
            _sourcePicker?.Close();
            _video?.Stop();
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
