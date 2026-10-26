using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RecordSummary;

/// <summary>Строка саммари: заголовок раздела, пункт списка, задача с галочкой или обычный текст.</summary>
public abstract record SummaryLine(int LineIndex);
public sealed record SummaryHeading(int LineIndex, string Text) : SummaryLine(LineIndex);
public sealed record SummaryBullet(int LineIndex, string Text) : SummaryLine(LineIndex);
public sealed record SummaryText(int LineIndex, string Text) : SummaryLine(LineIndex);
public sealed record SummaryTask(int LineIndex, bool Done, string Text, TimeSpan? At) : SummaryLine(LineIndex);

/// <summary>summary.md, разобранный для показа. Формат фиксирован промптом (см. Summarizer).</summary>
public sealed class SummaryDoc
{
    static readonly Regex Task = new(@"^\s*[-*] \[( |x|X)\] (.*?)\s*(?:\[(\d{1,2}:\d{2}(?::\d{2})?)\])?\s*$", RegexOptions.Compiled);
    static readonly Regex Bullet = new(@"^\s*(?:[-*]|\d+\.) (.*)$", RegexOptions.Compiled);

    readonly string _path;
    readonly string[] _lines;

    public string? Title { get; }
    public List<SummaryLine> Lines { get; } = [];

    SummaryDoc(string path)
    {
        _path = path;
        _lines = File.ReadAllLines(path);
        for (int i = 0; i < _lines.Length; i++)
        {
            var line = _lines[i].TrimEnd();
            if (line == "---") break;   // дальше — подпись "Саммари: модель"
            if (line.Length == 0) continue;
            if (line.StartsWith("# ")) { Title ??= line[2..].Trim(); continue; }
            if (line.StartsWith("## ")) { Lines.Add(new SummaryHeading(i, line[3..].Trim())); continue; }
            var t = Task.Match(line);
            if (t.Success)
            {
                Lines.Add(new SummaryTask(i, t.Groups[1].Value != " ", t.Groups[2].Value.Trim(' ', '—', '-'), ParseTime(t.Groups[3].Value)));
                continue;
            }
            var b = Bullet.Match(line);
            Lines.Add(b.Success ? new SummaryBullet(i, b.Groups[1].Value) : new SummaryText(i, line.Trim()));
        }
    }

    public static SummaryDoc? Load(string folder)
    {
        var path = Path.Combine(folder, "summary.md");
        return File.Exists(path) ? new SummaryDoc(path) : null;
    }

    static TimeSpan? ParseTime(string s)
    {
        if (s.Length == 0) return null;
        if (s.Count(c => c == ':') == 1) s = "0:" + s;
        return TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var t) ? t : null;
    }

    public int TaskCount => Lines.Count(l => l is SummaryTask);

    /// <summary>Первое предложение раздела "Кратко" — тема для старых саммари без заголовка-темы.</summary>
    public string? FirstSentence()
    {
        var text = Lines.SkipWhile(l => l is not SummaryHeading { Text: "Кратко" }).Skip(1).FirstOrDefault() switch
        {
            SummaryText t => t.Text,
            SummaryBullet b => b.Text,
            _ => null,
        };
        if (text == null) return null;
        int dot = text.IndexOfAny(['.', '!', '?']);
        return dot > 0 ? text[..dot] : text;
    }

    /// <summary>Галочка у задачи сохраняется прямо в summary.md.</summary>
    public void SetDone(SummaryTask task, bool done)
    {
        _lines[task.LineIndex] = Regex.Replace(_lines[task.LineIndex], @"\[( |x|X)\]", done ? "[x]" : "[ ]", RegexOptions.None, TimeSpan.FromSeconds(1));
        File.WriteAllLines(_path, _lines);
    }

    /// <summary>Текст для буфера обмена: Markdown без подписи модели.</summary>
    public string ToClipboard()
    {
        int end = Array.IndexOf(_lines, "---");
        return string.Join(Environment.NewLine, end < 0 ? _lines : _lines[..end]).Trim();
    }
}

public enum CallState { Recording, Processing, Done, NotProcessed, Failed }

/// <summary>Папка одного звонка: "yyyy-MM-dd HH-mm-ss Приложение".</summary>
public sealed class CallRecord : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Перерисовать строку в списке: состояние, ход обработки или название изменились.</summary>
    public void Notify() => PropertyChanged?.Invoke(this, new(""));

    const string MetaFile = "meta.json";

    public required string Folder { get; init; }
    public required DateTime StartedAt { get; init; }
    public required string App { get; init; }
    public TimeSpan Duration { get; init; }
    public string Title { get; set; } = "";
    public int TaskCount { get; init; }
    public int ShotCount { get; init; }
    public string? Error { get; init; }
    public CallState State { get; set; }
    /// <summary>Ход обработки для звонка в состоянии Processing.</summary>
    public string Progress { get; set; } = "";

    public bool HasError => Error != null && State is CallState.Done or CallState.Failed;

    /// <summary>Пока звонок пишется или обрабатывается, плеер не открываем: файлы заняты, а после распознавания wav сжимается.</summary>
    public bool HasAudio => State is not (CallState.Recording or CallState.Processing) && Duration > TimeSpan.Zero;

    public string DayLabel
    {
        get
        {
            var d = StartedAt.Date;
            if (d == DateTime.Today) return "Сегодня";
            if (d == DateTime.Today.AddDays(-1)) return "Вчера";
            var culture = CultureInfo.GetCultureInfo("ru-RU");
            return d.Year == DateTime.Today.Year ? d.ToString("d MMMM, dddd", culture) : d.ToString("d MMMM yyyy", culture);
        }
    }

    public string Heading => $"{App} · {StartedAt:HH:mm}";

    public string Meta => State switch
    {
        CallState.Recording => "Идёт запись",
        CallState.Processing => string.IsNullOrEmpty(Progress) ? "Обработка…" : Progress,
        CallState.Failed => "Не удалось обработать",
        CallState.NotProcessed => $"{FormatDuration(Duration)} · не обработан",
        _ when Error != null => $"{FormatDuration(Duration)} · саммари не получилось",
        _ => FormatDuration(Duration) + (TaskCount > 0 ? $" · задач: {TaskCount}" : "") + (ShotCount > 0 ? $" · снимков: {ShotCount}" : ""),
    };

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч {t.Minutes:D2} мин" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} мин" : $"{t.Seconds} с";

    /// <summary>Название, заданное вручную ("Переименовать").</summary>
    public void Rename(string title)
    {
        Title = title;
        var path = Path.Combine(Folder, MetaFile);
        if (string.IsNullOrWhiteSpace(title)) File.Delete(path);
        else File.WriteAllText(path, JsonSerializer.Serialize(new MetaJson(title)));
    }

    record MetaJson(string Title);

    static readonly Regex FolderName = new(@"^(\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}) (.+)$", RegexOptions.Compiled);

    public static CallRecord? Load(string folder)
    {
        var m = FolderName.Match(Path.GetFileName(folder));
        if (!m.Success || Tracks.Find(folder, Tracks.Mic) == null) return null;
        var started = DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        var app = m.Groups[2].Value;

        var summary = SummaryDoc.Load(folder);
        string? custom = null;
        try
        {
            var meta = Path.Combine(folder, MetaFile);
            if (File.Exists(meta)) custom = JsonSerializer.Deserialize<MetaJson>(File.ReadAllText(meta))?.Title;
        }
        catch { }
        var errorPath = Path.Combine(folder, CallProcessor.ErrorFile);
        var error = File.Exists(errorPath) ? File.ReadAllText(errorPath).Trim() : null;
        bool transcribed = File.Exists(Path.Combine(folder, "transcript.md"));

        // Заголовок summary.md у старых саммари — просто имя папки, а не тема.
        var title = custom
            ?? (summary?.Title is { } t && t != Path.GetFileName(folder) ? t : null)
            ?? summary?.FirstSentence()
            ?? (app == "Вручную" ? "Запись вручную" : $"Звонок в {app}");

        return new CallRecord
        {
            Folder = folder,
            StartedAt = started,
            App = app,
            Duration = Tracks.Duration(folder, Tracks.System),
            Title = title,
            TaskCount = summary?.TaskCount ?? 0,
            ShotCount = Screenshots.Count(folder),
            Error = error,
            // Транскрипт есть, а саммари упало — звонок всё равно можно читать и слушать.
            State = transcribed ? CallState.Done : error != null ? CallState.Failed : CallState.NotProcessed,
        };
    }

    public static List<CallRecord> LoadAll(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .Select(Load)
            .OfType<CallRecord>()
            .OrderByDescending(c => c.StartedAt)
            .ToList();
    }

    /// <summary>Поиск по названию, саммари и транскрипту (файлы читаются только при поиске).</summary>
    public bool Matches(string query)
    {
        if (Title.Contains(query, StringComparison.OrdinalIgnoreCase) || App.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var name in new[] { "summary.md", "transcript.md" })
        {
            var path = Path.Combine(Folder, name);
            try { if (File.Exists(path) && File.ReadAllText(path).Contains(query, StringComparison.OrdinalIgnoreCase)) return true; }
            catch { }
        }
        return false;
    }
}
