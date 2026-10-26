using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

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
    /// Снимает монитор, на котором окно в фокусе: нажатие на плашку фокус не забирает, так что это окно звонка
    /// (или то, что вы показываете). Плашка в снимок не попадает — она исключена из захвата экрана.
    /// </summary>
    public static string Capture(string folder, TimeSpan at)
    {
        var hwnd = GetForegroundWindow();
        var screen = hwnd != IntPtr.Zero ? Screen.FromHandle(hwnd) : Screen.FromPoint(Cursor.Position);
        var bounds = screen.Bounds;   // процесс PerMonitorV2 — настоящие пиксели монитора

        var dir = Path.Combine(folder, Dir);
        Directory.CreateDirectory(dir);
        var stem = $"{(int)at.TotalHours:D2}-{at.Minutes:D2}-{at.Seconds:D2}";
        var path = Path.Combine(dir, stem + ".jpg");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stem}_{i}.jpg");   // два снимка в одну секунду

        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var args = new EncoderParameters(1);
        args.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
        bmp.Save(path, jpeg, args);
        Log.Write($"Скриншот {Path.GetFileName(path)}: {screen.DeviceName} {bounds.Width}×{bounds.Height}");
        return path;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
}
