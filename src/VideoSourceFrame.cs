using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace RecordSummary;

/// <summary>Постоянная рамка источника: пропускает ввод и не попадает в захват.</summary>
public sealed class VideoSourceFrame : Window
{
    static readonly SolidColorBrush Idle = Frozen(0x60, 0xA5, 0xFA), Recording = Frozen(0xFA, 0xCC, 0x15);
    readonly Border _border = new() { BorderThickness = new Thickness(4), BorderBrush = Idle };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    VideoTarget? _target;
    IntPtr _handle;

    public VideoSourceFrame()
    {
        Title = "RecordSummary — источник видео";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowActivated = ShowInTaskbar = Focusable = IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = _border;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            // WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            SetWindowLong(_handle, -20, GetWindowLong(_handle, -20) | 0x080000A0);
            if (Environment.GetEnvironmentVariable("RECORDSUMMARY_SHOW_IN_CAPTURE") != "1"
                && !SetWindowDisplayAffinity(_handle, 0x11))
                Log.Write("Рамку источника видео не удалось исключить из захвата");
        };
        _timer.Tick += (_, _) => RefreshBounds();
        Closed += (_, _) => _timer.Stop();
    }

    public void SetTarget(VideoTarget? target, bool recording)
    {
        _target = target;
        _border.BorderBrush = recording ? Recording : Idle;
        if (target == null) { _timer.Stop(); Hide(); return; }
        RefreshBounds();
        _timer.Start();
    }

    void RefreshBounds()
    {
        if (_target == null) return;
        RECT bounds;
        if (_target.Window != IntPtr.Zero)
        {
            var hwnd = _target.Window;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)
                || (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
            { Hide(); return; }
            if (DwmGetWindowAttribute(hwnd, 9, out bounds, Marshal.SizeOf<RECT>()) != 0
                && !GetWindowRect(hwnd, out bounds)) { Hide(); return; }
        }
        else
        {
            var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _target.Display);
            if (screen == null) { Hide(); return; }
            var r = screen.Bounds;
            bounds = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        }
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) { Hide(); return; }
        if (!IsVisible) Show();
        // Физические координаты сохраняют совпадение рамки и окна при разных DPI мониторов.
        SetWindowPos(_handle, new IntPtr(-1), bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0010); // SWP_NOACTIVATE
    }

    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
