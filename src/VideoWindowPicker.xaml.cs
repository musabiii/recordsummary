using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Point = System.Windows.Point;

namespace RecordSummary;

/// <summary>Прозрачное окно перехватывает выбор, не передавая клик приложению под ним.</summary>
public partial class VideoWindowPicker : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(75) };
    Dictionary<IntPtr, VideoTarget> _targets = new();
    DateTime _lastRefresh;
    IntPtr _handle;
    VideoTarget? _hovered;

    public VideoTarget? SelectedTarget { get; private set; }
    public bool UseList { get; private set; }

    public VideoWindowPicker()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(_handle, -20, GetWindowLong(_handle, -20) | 0x00000080); // WS_EX_TOOLWINDOW
            if (Environment.GetEnvironmentVariable("RECORDSUMMARY_SHOW_IN_CAPTURE") != "1"
                && !SetWindowDisplayAffinity(_handle, 0x11))
                Log.Write("Подсветку выбора окна не удалось исключить из захвата");
            PlaceOverDesktop();
        };
        Loaded += (_, _) =>
        {
            PlaceOverDesktop();
            Activate();
            Focus();
            UpdateHoveredWindow();
            _timer.Start();
        };
        _timer.Tick += (_, _) => UpdateHoveredWindow();
        Closed += (_, _) => _timer.Stop();
    }

    void PlaceOverDesktop()
    {
        // Координаты Win32 физические: окно покрывает и мониторы слева/сверху от основного,
        // а подсветка переводится в DIP по текущему масштабу самого окна выбора.
        var bounds = SystemInformation.VirtualScreen;
        SetWindowPos(_handle, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010);
    }

    void UpdateHoveredWindow()
    {
        if (!GetCursorPos(out var cursor) || !GetWindowRect(_handle, out var overlay)) return;
        if ((DateTime.UtcNow - _lastRefresh).TotalSeconds >= 1)
        {
            _targets = VideoRecorder.ListTargets().Where(t => t.Window != IntPtr.Zero).ToDictionary(t => t.Window);
            _lastRefresh = DateTime.UtcNow;
        }

        _hovered = null;
        RECT hoveredBounds = default;
        // EnumWindows идёт в порядке наложения: выбираем верхнее доступное окно под мышью.
        EnumWindows((hwnd, _) =>
        {
            if (!_targets.TryGetValue(hwnd, out var target) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (DwmGetWindowAttribute(hwnd, 9, out RECT bounds, Marshal.SizeOf<RECT>()) != 0
                && !GetWindowRect(hwnd, out bounds)) return true;
            if (cursor.X < bounds.Left || cursor.X >= bounds.Right || cursor.Y < bounds.Top || cursor.Y >= bounds.Bottom) return true;
            _hovered = target;
            hoveredBounds = bounds;
            return false;
        }, IntPtr.Zero);

        var transform = HwndSource.FromHwnd(_handle).CompositionTarget.TransformFromDevice;
        if (_hovered != null)
        {
            var start = transform.Transform(new Point(hoveredBounds.Left - overlay.Left, hoveredBounds.Top - overlay.Top));
            var end = transform.Transform(new Point(hoveredBounds.Right - overlay.Left, hoveredBounds.Bottom - overlay.Top));
            Canvas.SetLeft(Highlight, start.X);
            Canvas.SetTop(Highlight, start.Y);
            Highlight.Width = Math.Max(0, end.X - start.X);
            Highlight.Height = Math.Max(0, end.Y - start.Y);
            Highlight.Visibility = Visibility.Visible;
        }
        else Highlight.Visibility = Visibility.Collapsed;
        SourceTitle.Text = _hovered?.Title ?? "Наведите на открытое окно · Esc — отменить";

        var work = Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea;
        var workStart = transform.Transform(new Point(work.Left - overlay.Left, work.Top - overlay.Top));
        var workEnd = transform.Transform(new Point(work.Right - overlay.Left, work.Bottom - overlay.Top));
        HintPanel.Width = Math.Max(240, Math.Min(560, workEnd.X - workStart.X - 32));
        Canvas.SetLeft(HintPanel, workStart.X + (workEnd.X - workStart.X - HintPanel.Width) / 2);
        Canvas.SetTop(HintPanel, workStart.Y + 16);
    }

    void Picker_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || HintPanel.IsMouseOver) return;
        e.Handled = true;
        UpdateHoveredWindow(); // окно могло переместиться или закрыться после последнего тика
        if (_hovered == null) return;
        try { VideoRecorder.ValidateTarget(_hovered); }
        catch (InvalidOperationException) { _lastRefresh = default; UpdateHoveredWindow(); return; }
        SelectedTarget = _hovered;
        DialogResult = true;
    }

    void Picker_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        DialogResult = false;
    }

    void List_Click(object sender, RoutedEventArgs e) { UseList = true; DialogResult = true; }
    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
