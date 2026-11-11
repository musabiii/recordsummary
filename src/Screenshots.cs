using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ScreenRecorderLib;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace RecordSummary;

/// <summary>Снимок экрана во время звонка; At — время записи (без пауз), как у реплик и плеера.</summary>
public sealed record Screenshot(string Path, TimeSpan At);

/// <summary>
/// Скриншоты звонка лежат в папке звонка: screenshots/чч-мм-сс.jpg. Время в имени — это и есть привязка
/// к записи, отдельных метаданных не нужно.
/// </summary>
public static class Screenshots
{
    public const string Dir = "screenshots";
    const long JpegQuality = 90;   // текст на слайдах читается, а снимок 4K весит ~1 МБ, а не 5–10 МБ PNG

    static readonly Regex Name = new(@"^(\d{2,})-(\d{2})-(\d{2})(?:_\d+)?\.(?:jpg|png)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<Screenshot> List(string folder)
    {
        var dir = Path.Combine(folder, Dir);
        if (!Directory.Exists(dir)) return [];
        var result = new List<Screenshot>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var m = Name.Match(Path.GetFileName(file));
            if (!m.Success) continue;
            var at = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            result.Add(new Screenshot(file, at));
        }
        return result.OrderBy(s => s.At).ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static int Count(string folder)
    {
        var dir = Path.Combine(folder, Dir);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Count(f => Name.IsMatch(Path.GetFileName(f))) : 0;
    }

    /// <summary>
    /// Снимок выбранного источника; wholeMonitor снимает весь монитор с этим источником.
    /// Окно снимается отдельно от рабочего стола, даже если его перекрывают другие окна.
    /// </summary>
    public static async Task<string> CaptureAsync(string folder, TimeSpan at, VideoTarget target, bool wholeMonitor = false)
    {
        var dir = Path.Combine(folder, Dir);
        Directory.CreateDirectory(dir);
        if (!wholeMonitor && target.Window != IntPtr.Zero)
        {
            var (source, _, _) = VideoRecorder.CreateSource(target);
            using var rendered = await Task.Run(() => RenderWindow(target.Window));
            if (rendered != null) return Save(rendered, dir, at, target.Title);

            var temporary = Path.Combine(dir, $".screenshot-{Guid.NewGuid():N}.png");
            var recorder = Recorder.CreateRecorder(new RecorderOptions
            {
                SourceOptions = new SourceOptions { RecordingSources = [source] },
                OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Screenshot },
                SnapshotOptions = new SnapshotOptions { SnapshotFormat = ScreenRecorderLib.ImageFormat.PNG },
                AudioOptions = new AudioOptions { IsAudioEnabled = false },
                MouseOptions = new MouseOptions { IsMousePointerEnabled = false },
            });
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            recorder.OnRecordingComplete += (_, _) => completed.TrySetResult();
            recorder.OnRecordingFailed += (_, e) => completed.TrySetException(new InvalidOperationException(e.Error));
            try
            {
                try
                {
                    recorder.Record(temporary);
                    await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
                finally
                {
                    // Нативный кодировщик нельзя освобождать внутри его callback.
                    await Task.Run(() => recorder.Dispose());
                }
                using var image = Image.FromFile(temporary);
                return Save(image, dir, at, target.Title);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        var screen = MonitorFor(target);
        var bounds = screen.Bounds; // PerMonitorV2: физические пиксели монитора
        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
        return Save(bmp, dir, at, screen.DeviceName);
    }

    static Bitmap? RenderWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var bounds) || IsHungAppWindow(hwnd)) return null;
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) return null;
        var image = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            using (var graphics = Graphics.FromImage(image))
            {
                var dc = graphics.GetHdc();
                bool success;
                try { success = PrintWindow(hwnd, dc, 2); } // PW_RENDERFULLCONTENT
                finally { graphics.ReleaseHdc(dc); }
                if (!success) { image.Dispose(); return null; }
            }
            // Некоторые аппаратные окна не поддерживают PrintWindow: для них используем WGC.
            bool content = false;
            for (int y = 0; y < height && !content; y += Math.Max(1, height / 16))
                for (int x = 0; x < width && !content; x += Math.Max(1, width / 16))
                {
                    var pixel = image.GetPixel(x, y);
                    content = pixel.R != 0 || pixel.G != 0 || pixel.B != 0;
                }
            if (!content) { image.Dispose(); return null; }
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    static Screen MonitorFor(VideoTarget target)
    {
        if (target.Window != IntPtr.Zero)
        {
            if (!IsWindow(target.Window)) throw new InvalidOperationException("Окно источника закрыто. Выберите другое окно.");
            return Screen.FromHandle(target.Window);
        }
        return Screen.AllScreens.FirstOrDefault(s => s.DeviceName == target.Display)
            ?? throw new InvalidOperationException("Монитор источника отключён. Выберите другой источник.");
    }

    static string Save(Image image, string dir, TimeSpan at, string source)
    {
        var stem = $"{(int)at.TotalHours:D2}-{at.Minutes:D2}-{at.Seconds:D2}";
        var path = Path.Combine(dir, stem + ".jpg");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stem}_{i}.jpg");   // два снимка в одну секунду

        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var args = new EncoderParameters(1);
        args.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
        image.Save(path, jpeg, args);
        Log.Write($"Скриншот {Path.GetFileName(path)}: {source} {image.Width}×{image.Height}");
        return path;
    }

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr hwnd);
}
