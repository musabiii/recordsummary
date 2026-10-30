using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RecordSummary;

/// <summary>
/// Размер окон в XAML рассчитан на большой монитор, а WPF при мониторах с разным масштабом (125 % и 250 %)
/// ставит окно по системному DPI и промахивается: оно вылезает за край. Окно открывается на мониторе,
/// где курсор (там пользователь его и вызвал), ужимается до рабочей области и встаёт по центру.
/// </summary>
static class WindowFit
{
    const double Margin = 16;   // от краёв рабочей области, DIP

    /// <summary>Вызывать в конструкторе окна, до Show.</summary>
    public static void Fit(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.SourceInitialized += (_, _) => Place(w);
    }

    /// <summary>Окно уже создано, но ещё не показано. Координаты — физические пиксели (приложение PerMonitorV2).</summary>
    static void Place(Window w)
    {
        if (!GetCursorPos(out var cursor)) return;
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        double scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        var work = info.rcWork;
        int workW = work.Right - work.Left, workH = work.Bottom - work.Top;
        var hwnd = new WindowInteropHelper(w).Handle;

        // Сначала только переносим на этот монитор: если у него другой масштаб, WPF получит WM_DPICHANGED
        // и пересчитает размер сам. Иначе он применит изменение DPI уже к нашему размеру и окно вырастет.
        SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        double maxW = workW / scale - 2 * Margin, maxH = workH / scale - 2 * Margin;
        w.MinWidth = Math.Min(w.MinWidth, maxW);
        w.MinHeight = Math.Min(w.MinHeight, maxH);
        int width = (int)(Math.Min(w.Width, maxW) * scale), height = (int)(Math.Min(w.Height, maxH) * scale);
        SetWindowPos(hwnd, IntPtr.Zero, work.Left + (workW - width) / 2, work.Top + (workH - height) / 2, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
