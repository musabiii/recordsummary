using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace RecordSummary;

/// <summary>
/// Плашка поверх всех окон, как в Notion: пока идёт звонок — предлагает записать, во время записи —
/// таймер, уровни обеих сторон и кнопки. Не забирает фокус у окна звонка и не видна в Alt+Tab.
/// </summary>
public partial class RecordingWidget : Window
{
    static readonly SolidColorBrush Red = Frozen(0xEB, 0x40, 0x34), Amber = Frozen(0xF5, 0xA6, 0x23), Gray = Frozen(0x8C, 0x8C, 0x91);

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly Storyboard _pulse;
    CallRecorder? _recorder;
    string _app = "";
    double _micLevel, _systemLevel;

    /// <summary>"Записать" в режиме предложения.</summary>
    public event Action? RecordRequested;
    public event Action? PauseToggled;
    public event Action? StopRequested;
    /// <summary>Крестик: скрыть плашку (запись при этом продолжается) или отказаться от записи звонка.</summary>
    public event Action? Dismissed;
    /// <summary>Плашку перетащили — запомнить место.</summary>
    public event Action<double, double>? Moved;

    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public RecordingWidget(double? left, double? top)
    {
        InitializeComponent();

        var fade = new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(fade, Dot);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        _pulse = new Storyboard { Children = { fade } };

        _timer.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) => MakeNoActivate();
        Loaded += (_, _) => Place(left, top);
        // Ширина меняется между "Записать?" и записью — держим правый край на месте, чтобы плашка не уезжала за экран.
        SizeChanged += (_, e) => { if (IsLoaded && e.PreviousSize.Width > 0) Left -= e.NewSize.Width - e.PreviousSize.Width; };
        Closed += (_, _) => _timer.Stop();
    }

    // ---------- Режимы ----------

    /// <summary>Звонок замечен, запись не идёт: предложить записать.</summary>
    public void ShowOffer(string app)
    {
        _recorder = null;
        _app = app;
        _timer.Stop();
        _pulse.Stop();
        Dot.Opacity = 1;
        Dot.Fill = Amber;
        AppText.Text = "Звонок в " + app;
        TimeText.Text = "Записать?";
        TimeText.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        Meters.Visibility = PauseButton.Visibility = StopButton.Visibility = Visibility.Collapsed;
        RecordButton.Visibility = Visibility.Visible;
        CloseButton.ToolTip = "Не записывать этот звонок";
    }

    public void ShowRecording(string app, CallRecorder recorder)
    {
        _recorder = recorder;
        _app = app;
        TimeText.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        Meters.Visibility = PauseButton.Visibility = StopButton.Visibility = Visibility.Visible;
        RecordButton.Visibility = Visibility.Collapsed;
        CloseButton.ToolTip = "Скрыть окошко — запись продолжится";
        UpdatePaused();
        Tick();
        _timer.Start();
    }

    /// <summary>Вызывается после паузы/продолжения: точка, подпись и значок кнопки.</summary>
    public void UpdatePaused()
    {
        if (_recorder == null) return;
        bool paused = _recorder.IsPaused;
        Dot.Fill = paused ? Gray : Red;
        if (paused) { _pulse.Stop(); Dot.Opacity = 1; }
        else _pulse.Begin();
        AppText.Text = (paused ? "Пауза · " : "Запись · ") + _app;
        PauseIcon.Text = paused ? "" : "";
        PauseButton.ToolTip = paused ? "Продолжить запись" : "Пауза";
        System.Windows.Automation.AutomationProperties.SetName(PauseButton, paused ? "Продолжить" : "Пауза");
    }

    void Tick()
    {
        if (_recorder == null) return;
        var t = _recorder.Elapsed;
        TimeText.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        // Пики быстрые и мелкие: корень растягивает тихую речь, плавный спад не даёт полоске мигать.
        _micLevel = Math.Max(Math.Sqrt(_recorder.MicLevel), _micLevel * 0.8);
        _systemLevel = Math.Max(Math.Sqrt(_recorder.SystemLevel), _systemLevel * 0.8);
        MicMeter.Value = _micLevel;
        SystemMeter.Value = _systemLevel;
    }

    // ---------- Кнопки и перетаскивание ----------

    void Record_Click(object sender, RoutedEventArgs e) => RecordRequested?.Invoke();
    void Pause_Click(object sender, RoutedEventArgs e) => PauseToggled?.Invoke();
    void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    void Close_Click(object sender, RoutedEventArgs e) => Dismissed?.Invoke();

    void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        DragMove();   // возвращается, когда кнопку отпустили
        Moved?.Invoke(Left, Top);
    }

    /// <summary>Сохранённое место, если оно на одном из мониторов, иначе правый нижний угол над панелью задач.</summary>
    void Place(double? left, double? top)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (left is { } l && top is { } t && screen.Contains(new System.Windows.Point(l + 40, t + 40)))
        {
            Left = l;
            Top = t;
            return;
        }
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth - 8;
        Top = work.Bottom - ActualHeight - 8;
    }

    // WS_EX_NOACTIVATE: клик по плашке не уводит фокус из Zoom/Teams; WS_EX_TOOLWINDOW: нет в Alt+Tab.
    const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    void MakeNoActivate()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }
}
