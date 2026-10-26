using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ScreenRecorderLib;

namespace RecordSummary;

/// <summary>Фрагмент видео экрана; Start и End — время записи звонка (без пауз), как у реплик и скриншотов.</summary>
public sealed record VideoSegment(string Path, TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
    public bool Contains(TimeSpan t) => t >= Start && t < End;
}

/// <summary>
/// Видео звонка лежит в video/: "чч-мм-сс--чч-мм-сс.mp4" — начало и конец фрагмента во времени записи.
/// Пока фрагмент пишется, у него расширение .part.mp4 — такие файлы окно записей не показывает.
/// </summary>
public static class Videos
{
    public const string Dir = "video";

    static readonly Regex Name = new(@"^(\d{2,})-(\d{2})-(\d{2})--(\d{2,})-(\d{2})-(\d{2})\.mp4$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<VideoSegment> List(string folder)
    {
        var dir = Path.Combine(folder, Dir);
        if (!Directory.Exists(dir)) return [];
        var result = new List<VideoSegment>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.mp4"))
        {
            var m = Name.Match(Path.GetFileName(file));
            if (!m.Success) continue;
            TimeSpan T(int g) => new(int.Parse(m.Groups[g].Value), int.Parse(m.Groups[g + 1].Value), int.Parse(m.Groups[g + 2].Value));
            result.Add(new VideoSegment(file, T(1), T(4)));
        }
        return result.OrderBy(v => v.Start).ToList();
    }

    public static string Stamp(TimeSpan t) => $"{(int)t.TotalHours:D2}-{t.Minutes:D2}-{t.Seconds:D2}";
}

/// <summary>
/// Запись экрана во время звонка (ScreenRecorderLib: Windows.Graphics.Capture + аппаратный H.264 через Media Foundation).
/// Пишет только картинку — звук остаётся в наших дорожках. Каждое включение — отдельный фрагмент.
/// </summary>
public sealed class VideoRecorder : IDisposable
{
    readonly string _folder;
    readonly Func<TimeSpan> _elapsed;
    Recorder? _recorder;
    string _partPath = "";
    TimeSpan _start;

    public bool IsRecording => _recorder != null;

    /// <summary>Описание того, что снимается: "окно «Zoom Meeting»" или "монитор DISPLAY2".</summary>
    public string Source { get; private set; } = "";

    /// <summary>Запись упала (окно закрыли, кодировщик недоступен…). Приходит не из UI-потока.</summary>
    public event Action<string>? Failed;

    public VideoRecorder(string folder, Func<TimeSpan> elapsed)
    {
        _folder = folder;
        _elapsed = elapsed;
    }

    /// <param name="callKey">Путь к exe приложения звонка (из детектора) — чтобы снимать его окно.</param>
    public void Start(Config cfg, string? callKey)
    {
        if (_recorder != null) return;
        var (source, size, description) = PickSource(cfg, callKey);
        Source = description;

        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = [source] },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                // Не больше VideoMaxHeight по высоте, пропорции сохраняются; чётные размеры — требование H.264.
                OutputFrameSize = Fit(size, cfg.VideoMaxHeight),
                Stretch = StretchMode.Uniform,
            },
            AudioOptions = new AudioOptions { IsAudioEnabled = false },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder { BitrateMode = H264BitrateControlMode.Quality, EncoderProfile = H264Profile.Main },
                Quality = 70,
                Framerate = cfg.VideoFramerate,
                IsFixedFramerate = false,          // на статичной картинке кадры не пишутся — файл меньше
                IsHardwareEncodingEnabled = true,
                IsMp4FastStartEnabled = true,      // индекс в начале файла — окно записей открывает и перематывает сразу
            },
            MouseOptions = new MouseOptions { IsMousePointerEnabled = true },
        };

        _start = _elapsed();
        var dir = Path.Combine(_folder, Videos.Dir);
        Directory.CreateDirectory(dir);
        _partPath = Path.Combine(dir, Videos.Stamp(_start) + ".part.mp4");
        var rec = Recorder.CreateRecorder(options);
        var part = _partPath;
        var start = _start;
        rec.OnRecordingFailed += (_, e) =>
        {
            Log.Write($"Видео: ошибка записи {e.Error}");
            Finish(part, start, _elapsed());
            Failed?.Invoke(e.Error);
        };
        rec.OnRecordingComplete += (_, _) => Log.Write($"Видео: фрагмент {Path.GetFileName(part)} записан");
        rec.Record(_partPath);
        _recorder = rec;
        Log.Write($"Видео: начато с {start:hh\\:mm\\:ss}, {description}, {options.OutputOptions.OutputFrameSize?.Width}×{options.OutputOptions.OutputFrameSize?.Height} @ {cfg.VideoFramerate} к/с");
    }

    public void Pause() => _recorder?.Pause();
    public void Resume() => _recorder?.Resume();

    /// <summary>Закончить фрагмент. Кодировщик дописывает файл в фоне; когда закончит — файл получает имя с концом фрагмента.</summary>
    public void Stop()
    {
        var rec = _recorder;
        if (rec == null) return;
        _recorder = null;
        var part = _partPath;
        var start = _start;
        var end = _elapsed();
        rec.OnRecordingComplete += (_, _) =>
        {
            Finish(part, start, end);
            rec.Dispose();
        };
        rec.Stop();
    }

    static void Finish(string part, TimeSpan start, TimeSpan end)
    {
        try
        {
            if (!File.Exists(part)) return;
            if (end <= start) end = start + TimeSpan.FromSeconds(1);
            var final = Path.Combine(Path.GetDirectoryName(part)!, $"{Videos.Stamp(start)}--{Videos.Stamp(end)}.mp4");
            File.Move(part, final, true);
            Log.Write($"Видео: {Path.GetFileName(final)}, {new FileInfo(final).Length / 1048576.0:F1} МБ");
        }
        catch (Exception e) { Log.Write($"Видео: не удалось переименовать {part}: {e.Message}"); }
    }

    public void Dispose() => Stop();

    // ---------- Что снимать ----------

    /// <summary>
    /// Окно приложения звонка (самое большое видимое окно его процессов), иначе — монитор с окном в фокусе.
    /// Окно пишется, даже если его перекрыли другим окном.
    /// </summary>
    static (RecordingSourceBase Source, Size Size, string Description) PickSource(Config cfg, string? callKey)
    {
        if (cfg.VideoSource == "window" && FindCallWindow(callKey) is { } window)
        {
            GetWindowRect(window.Handle, out var r);
            var source = new WindowRecordingSource(window.Handle) { IsBorderRequired = false, IsCursorCaptureEnabled = true };
            return (source, new Size(r.Right - r.Left, r.Bottom - r.Top), $"окно «{window.Title}»");
        }
        var hwnd = GetForegroundWindow();
        var screen = hwnd != IntPtr.Zero ? Screen.FromHandle(hwnd) : Screen.PrimaryScreen!;
        var display = new DisplayRecordingSource(screen.DeviceName) { IsBorderRequired = false, IsCursorCaptureEnabled = true };
        return (display, screen.Bounds.Size, $"монитор {screen.DeviceName.TrimStart('\\', '.')}");
    }

    static RecordableWindow? FindCallWindow(string? callKey)
    {
        if (string.IsNullOrEmpty(callKey) || !callKey.Contains('\\')) return null;   // пакеты Microsoft Store — по пути не найти
        var pids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(callKey)))
        {
            try { if (string.Equals(p.MainModule?.FileName, callKey, StringComparison.OrdinalIgnoreCase)) pids.Add(p.Id); }
            catch { pids.Add(p.Id); }   // нет доступа к модулю — имя exe совпало, этого достаточно
            finally { p.Dispose(); }
        }
        if (pids.Count == 0) return null;
        return Recorder.GetWindows()
            .Where(w => w.Pid is { } pid && pids.Contains(pid) && w.IsValidWindow() && !w.IsMinmimized())
            .Select(w => (Window: w, Area: GetWindowRect(w.Handle, out var r) ? (long)(r.Right - r.Left) * (r.Bottom - r.Top) : 0))
            .Where(x => x.Area > 200 * 150)                     // не служебные окошки и всплывающие уведомления
            .OrderByDescending(x => x.Area)
            .Select(x => x.Window)
            .FirstOrDefault();
    }

    static ScreenSize Fit(Size size, int maxHeight)
    {
        double w = Math.Max(2, size.Width), h = Math.Max(2, size.Height);
        if (h > maxHeight) { w = w * maxHeight / h; h = maxHeight; }
        return new ScreenSize(Math.Floor(w / 2) * 2, Math.Floor(h / 2) * 2);
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
}
