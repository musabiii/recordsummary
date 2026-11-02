using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ScreenRecorderLib;

namespace RecordSummary;

/// <summary>Источник, выбранный пользователем на время текущего звонка.</summary>
public sealed record VideoTarget(IntPtr Window, string? Display, string Title)
{
    public override string ToString() => Title;
}

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

    static readonly Regex Name = new(@"^(\d{2,})-(\d{2})-(\d{2})(?:-(\d{3}))?--(\d{2,})-(\d{2})-(\d{2})(?:-(\d{3}))?\.mp4$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<VideoSegment> List(string folder)
    {
        var dir = Path.Combine(folder, Dir);
        if (!Directory.Exists(dir)) return [];
        var result = new List<VideoSegment>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.mp4"))
        {
            var m = Name.Match(Path.GetFileName(file));
            if (!m.Success) continue;
            TimeSpan T(int g) => new TimeSpan(int.Parse(m.Groups[g].Value), int.Parse(m.Groups[g + 1].Value), int.Parse(m.Groups[g + 2].Value))
                + TimeSpan.FromMilliseconds(m.Groups[g + 3].Success ? int.Parse(m.Groups[g + 3].Value) : 0);
            result.Add(new VideoSegment(file, T(1), T(5)));
        }
        return result.OrderBy(v => v.Start).ToList();
    }

    public static string Stamp(TimeSpan t) => $"{(int)t.TotalHours:D2}-{t.Minutes:D2}-{t.Seconds:D2}-{t.Milliseconds:D3}";
}

/// <summary>
/// Запись экрана во время звонка (ScreenRecorderLib: Windows.Graphics.Capture + аппаратный H.264 через Media Foundation).
/// Пишет только картинку — звук остаётся в наших дорожках. Каждое включение — отдельный фрагмент.
/// </summary>
public sealed class VideoRecorder : IDisposable
{
    readonly string _folder;
    readonly Func<TimeSpan> _elapsed;
    sealed class Session(Recorder recorder, string path, TimeSpan start)
    {
        public readonly Recorder Recorder = recorder;
        public readonly string Path = path;
        public readonly TimeSpan Start = start;
        public TimeSpan? End;
        public int Completed;
        public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    Session? _session;
    Task _finishing = Task.CompletedTask;

    public bool IsRecording => _session != null;

    /// <summary>Описание того, что снимается: "окно «Zoom Meeting»" или "монитор DISPLAY2".</summary>
    public string Source { get; private set; } = "";
    public VideoTarget? Target { get; private set; }

    /// <summary>Запись упала (окно закрыли, кодировщик недоступен…). Приходит не из UI-потока.</summary>
    public event Action<string>? Failed;

    public VideoRecorder(string folder, Func<TimeSpan> elapsed)
    {
        _folder = folder;
        _elapsed = elapsed;
    }

    /// <param name="callKey">Путь к exe приложения звонка (из детектора) — чтобы снимать его окно.</param>
    public void Start(Config cfg, string? callKey, VideoTarget? target = null)
    {
        if (_session != null) return;
        target ??= PickTarget(cfg, callKey);
        var (source, size, description) = CreateSource(target);
        Source = description;
        Target = target;

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

        var start = _elapsed();
        var dir = Path.Combine(_folder, Videos.Dir);
        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, Videos.Stamp(start) + $"-{Guid.NewGuid():N}.part.mp4");
        var rec = Recorder.CreateRecorder(options);
        var session = new Session(rec, part, start);
        rec.OnRecordingFailed += (_, e) =>
        {
            Log.Write($"Видео: ошибка записи {e.Error}");
            bool current = ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session);
            Complete(session);
            if (current) Failed?.Invoke(e.Error);
        };
        rec.OnRecordingComplete += (_, _) => Complete(session);
        _session = session;
        _finishing = session.Finished.Task;
        try { rec.Record(part); }
        catch
        {
            Interlocked.CompareExchange(ref _session, null, session);
            Complete(session);
            throw;
        }
        Log.Write($"Видео: начато с {start:hh\\:mm\\:ss}, {description}, {options.OutputOptions.OutputFrameSize?.Width}×{options.OutputOptions.OutputFrameSize?.Height} @ {cfg.VideoFramerate} к/с");
    }

    public void Pause() => _session?.Recorder.Pause();
    public void Resume() => _session?.Recorder.Resume();

    void Complete(Session session)
    {
        if (Interlocked.Exchange(ref session.Completed, 1) != 0) return;
        var end = session.End ?? _elapsed();
        // Callback приходит из потока кодировщика. Dispose ждёт этот поток,
        // поэтому освобождаем его снаружи callback, а файл переименовываем уже после закрытия.
        _ = Task.Run(() =>
        {
            try
            {
                session.Recorder.Dispose();
                Finish(session.Path, session.Start, end);
                session.Finished.TrySetResult();
            }
            catch (Exception e)
            {
                Log.Write($"Видео: ошибка завершения фрагмента: {e.Message}");
                session.Finished.TrySetException(e);
            }
        });
    }

    /// <summary>Закончить фрагмент. Кодировщик дописывает файл в фоне; когда закончит — файл получает имя с концом фрагмента.</summary>
    public void Stop()
    {
        var session = _session;
        if (session == null) return;
        session.End = _elapsed();
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session)) return;
        try { session.Recorder.Stop(); }
        catch { Complete(session); throw; }
    }

    public async Task StopAndWaitAsync()
    {
        Stop();
        await _finishing.WaitAsync(TimeSpan.FromSeconds(15));
    }

    static void Finish(string part, TimeSpan start, TimeSpan end)
    {
        try
        {
            if (!File.Exists(part)) return;
            // Смена окна во время паузы не должна создавать пересекающийся фрагмент.
            if (end <= start) { File.Delete(part); return; }
            var final = Path.Combine(Path.GetDirectoryName(part)!, $"{Videos.Stamp(start)}--{Videos.Stamp(end)}.mp4");
            File.Move(part, final);
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
    public static List<VideoTarget> ListTargets()
    {
        var windows = Recorder.GetWindows()
            .Where(w => w.Pid != Environment.ProcessId && w.IsValidWindow() && !w.IsMinmimized() && !string.IsNullOrWhiteSpace(w.Title))
            .Select(w => new VideoTarget(w.Handle, null, $"Окно: {w.Title}"))
            .OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase);
        return Screen.AllScreens.Select(s => new VideoTarget(IntPtr.Zero, s.DeviceName,
            $"Монитор {s.DeviceName.TrimStart('\\', '.')} · {s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " (основной)" : "")))
            .Concat(windows).ToList();
    }

    static VideoTarget PickTarget(Config cfg, string? callKey)
    {
        if (cfg.VideoSource == "window" && FindCallWindow(callKey) is { } window)
            return new VideoTarget(window.Handle, null, $"Окно: {window.Title}");
        var hwnd = GetForegroundWindow();
        var screen = hwnd != IntPtr.Zero ? Screen.FromHandle(hwnd) : Screen.PrimaryScreen!;
        return new VideoTarget(IntPtr.Zero, screen.DeviceName, $"Монитор {screen.DeviceName.TrimStart('\\', '.')}");
    }

    public static void ValidateTarget(VideoTarget target) => CreateSource(target);

    static (RecordingSourceBase Source, Size Size, string Description) CreateSource(VideoTarget target)
    {
        if (target.Window != IntPtr.Zero)
        {
            if (!IsWindow(target.Window) || IsIconic(target.Window) || !GetWindowRect(target.Window, out var r))
                throw new InvalidOperationException("Выбранное окно закрыто или свёрнуто. Выберите другое окно.");
            var source = new WindowRecordingSource(target.Window) { IsBorderRequired = true, IsCursorCaptureEnabled = true };
            return (source, new Size(r.Right - r.Left, r.Bottom - r.Top), target.Title);
        }
        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == target.Display)
            ?? throw new InvalidOperationException("Выбранный монитор отключён. Выберите другой источник.");
        var display = new DisplayRecordingSource(screen.DeviceName)
        {
            RecorderApi = RecorderApi.WindowsGraphicsCapture,
            IsBorderRequired = true,
            IsCursorCaptureEnabled = true,
        };
        return (display, screen.Bounds.Size, target.Title);
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
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
}
