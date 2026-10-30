using System.Diagnostics;
using SherpaOnnx;

namespace RecordSummary;

public record SpeakerTurn(TimeSpan Start, TimeSpan End, string Speaker);

/// <summary>
/// Кто из собеседников когда говорит: на системной дорожке все участники звонка смешаны в один звук.
/// sherpa-onnx: pyannote находит отрезки речи, эмбеддинги голоса кластеризуются по людям.
/// Работает на CPU и до загрузки whisper, модели — ~32 МБ, скачиваются сами.
/// </summary>
public static class Diarizer
{
    const string SegmentationModel = "models/sherpa-onnx-pyannote-segmentation-3-0.onnx";
    const string SegmentationUrl = "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx";
    const string EmbeddingModel = "models/wespeaker_en_voxceleb_resnet34_LM.onnx";
    /// <summary>Шаг окна pyannote (10 с) в долях окна. По умолчанию в sherpa 0,1: каждый кусок голоса считается 10 раз, и 25 мин звонка делятся 4,5 мин вместо минуты.</summary>
    const float WindowShift = 0.5f;
    const string EmbeddingUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx";

    /// <summary>Реплики собеседников по порядку; null — разделить не получилось, все останутся одним "Собеседником".</summary>
    public static async Task<SpeakerTurns?> RunAsync(float[] audio, Config cfg, IProgress<string> status, CancellationToken ct = default)
    {
        if (audio.Length == 0) return null;
        try
        {
            var segmentation = await EnsureAsync(SegmentationModel, SegmentationUrl, status);
            var embedding = await EnsureAsync(EmbeddingModel, EmbeddingUrl, status);
            return await Task.Run(() => Run(audio, segmentation, embedding, cfg, status), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Без разделения транскрипт всё равно полезен — обработку звонка не роняем.
            Log.Write($"Не удалось разделить собеседников: {e}");
            return null;
        }
    }

    static SpeakerTurns Run(float[] audio, string segmentation, string embedding, Config cfg, IProgress<string> status)
    {
        var sw = Stopwatch.StartNew();
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentation;
        config.Segmentation.Pyannote.WindowShiftRatio = WindowShift;
        config.Segmentation.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        config.Embedding.Model = embedding;
        config.Embedding.NumThreads = config.Segmentation.NumThreads;
        // Число участников, если известно, надёжнее порога: порог — насколько разными должны быть голоса.
        config.Clustering.NumClusters = cfg.SpeakerCount > 0 ? cfg.SpeakerCount : -1;
        config.Clustering.Threshold = cfg.SpeakerThreshold;
        config.MinDurationOn = 0.3f;
        config.MinDurationOff = 0.5f;

        using var sd = new OfflineSpeakerDiarization(config);
        OfflineSpeakerDiarizationProgressCallback progress = (done, total, _) =>
        {
            status.Report($"Разделяю собеседников по голосам: {done * 100 / Math.Max(1, total)}%");
            return 0;
        };
        var segments = sd.ProcessWithCallback(audio, progress, IntPtr.Zero);
        GC.KeepAlive(progress);

        // Номера кластеров — в порядке первой реплики: "Собеседник 1" заговорил первым.
        var order = segments.OrderBy(s => s.Start).Select(s => s.Speaker).Distinct().ToList();
        var turns = segments
            .OrderBy(s => s.Start)
            .Select(s => new SpeakerTurn(TimeSpan.FromSeconds(s.Start), TimeSpan.FromSeconds(s.End),
                order.Count > 1 ? $"{Transcript.Them} {order.IndexOf(s.Speaker) + 1}" : Transcript.Them))
            .ToList();
        Log.Write($"Собеседников по голосу: {order.Count}, {turns.Count} отрезков за {sw.Elapsed.TotalSeconds:F1} с");
        return new SpeakerTurns(turns);
    }

    static async Task<string> EnsureAsync(string model, string url, IProgress<string> status)
    {
        var path = Config.Resolve(model);
        if (path != null) return path;
        path = Config.DefaultPath(model);
        status.Report("Скачиваю модель разделения голосов…");
        Log.Write($"Модель {Path.GetFileName(model)} не найдена, скачиваю в {path}");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        await Config.DownloadAsync(() => http.GetStreamAsync(url), path);
        return path;
    }
}

/// <summary>Режет отрезки речи по сменам говорящего, чтобы в один кусок для whisper не попали двое.</summary>
public sealed class SpeakerTurns(List<SpeakerTurn> turns)
{
    /// <summary>Резать ближе к краю отрезка нет смысла: выйдет обрывок короче слова.</summary>
    static readonly TimeSpan MinPiece = TimeSpan.FromSeconds(0.5);

    /// <summary>Где сменяется говорящий: посередине паузы между репликами, а если перебили — с начала перебившего.</summary>
    readonly List<TimeSpan> _changes = turns.Zip(turns.Skip(1))
        .Where(p => p.First.Speaker != p.Second.Speaker)
        .Select(p => p.Second.Start > p.First.End ? (p.First.End + p.Second.Start) / 2 : p.Second.Start)
        .ToList();

    public IEnumerable<(TimeSpan From, TimeSpan To, string Speaker)> Split(TimeSpan from, TimeSpan to)
    {
        var start = from;
        foreach (var at in _changes)
        {
            if (at - start < MinPiece) continue;
            if (to - at < MinPiece) break;
            yield return (start, at, SpeakerOf(start, at));
            start = at;
        }
        yield return (start, to, SpeakerOf(start, to));
    }

    /// <summary>Чья речь больше всего перекрывает отрезок; если ничья (VAD нашёл речь, pyannote — нет) — ближайшая.</summary>
    string SpeakerOf(TimeSpan from, TimeSpan to)
    {
        if (turns.Count == 0) return Transcript.Them;
        var overlap = turns
            .GroupBy(t => t.Speaker)
            .Select(g => (Speaker: g.Key, Seconds: g.Sum(t => Math.Max(0, ((t.End < to ? t.End : to) - (t.Start > from ? t.Start : from)).TotalSeconds))))
            .MaxBy(x => x.Seconds);
        if (overlap.Seconds > 0) return overlap.Speaker;
        return turns.MinBy(t => t.End < from ? (from - t.End).Ticks : t.Start > to ? (t.Start - to).Ticks : 0)!.Speaker;
    }
}
