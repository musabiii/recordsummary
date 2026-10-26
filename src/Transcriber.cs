using System.Diagnostics;
using System.Text.RegularExpressions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace RecordSummary;

public record Utterance(TimeSpan Start, TimeSpan End, string Speaker, string Text);

/// <summary>
/// whisper.cpp через Whisper.net. Дорожка сначала режется VAD на куски речи: на долгой тишине
/// (собеседник молчит, пока говорите вы) whisper выдумывает фразы, а так ещё и быстрее.
/// Модель грузится на время обработки звонка и освобождается — приложение висит в трее весь день.
/// </summary>
public sealed class Transcriber : IDisposable
{
    public const int SampleRate = 16000;
    const string VadModel = "models/ggml-silero-v5.1.2.bin";
    // Куски речи склеиваются, пока пауза короче MaxGap и кусок не длиннее окна whisper.
    // Склеивать через длинные паузы нельзя: whisper сливает соседние реплики в один сегмент, и диалог перемешивается.
    static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(1.5), MaxChunk = TimeSpan.FromSeconds(28);
    // Энкодер whisper: 1500 позиций на 30 с аудио, т.е. 50 в секунду. Короткий кусок кодируем окном
    // по его длине, а не полным: реплика в 5 с распознаётся в разы быстрее.
    const int CtxPerSecond = 50, FullCtx = 1500;
    static readonly int[] CtxBuckets = [256, 512, 768, 1024, FullCtx];

    // Типичные галлюцинации whisper на тишине/шуме (субтитры из обучающих данных).
    static readonly Regex Hallucination = new(
        @"^\s*(продолжение следует|субтитры|редактор субтитров|спасибо за (просмотр|внимание)|thank you( for watching)?|thanks for watching|\[.*\]|\(.*\)|♪+)[\s.!…]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly WhisperFactory _factory;
    readonly Config _cfg;
    readonly Dictionary<int, WhisperProcessor> _processors = new();
    readonly WhisperVadFactory _vadFactory;
    readonly WhisperVadProcessor _vad;

    public string Backend { get; }

    Transcriber(string modelPath, string vadPath, Config cfg)
    {
        // Нативная библиотека выбирается один раз на процесс.
        if (RuntimeOptions.LoadedLibrary == null)
            RuntimeOptions.RuntimeLibraryOrder = cfg.UseGpu ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu] : [RuntimeLibrary.Cpu];

        _cfg = cfg;
        _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = cfg.UseGpu, GpuDevice = cfg.GpuDevice });
        _vadFactory = WhisperVadFactory.FromPath(vadPath);
        _vad = _vadFactory.CreateBuilder()
            .WithUseGpu(false)
            .WithThreshold(0.5f)
            .WithMinSpeechDuration(TimeSpan.FromMilliseconds(250))
            .Build();
        Processor(FullCtx);
        Backend = RuntimeOptions.LoadedLibrary?.ToString() ?? "?";
    }

    WhisperProcessor Processor(int ctx)
    {
        if (_processors.TryGetValue(ctx, out var p)) return p;
        var builder = _factory.CreateBuilder()
            .WithLanguage(_cfg.Language)
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
            .WithNoContext();   // без контекста whisper не зацикливается на повторе предыдущей фразы
        // С укороченным окном whisper после конца речи начинает заново и повторяет фразу;
        // один сегмент на кусок это обрывает (куски и так не длиннее 28 с).
        if (ctx < FullCtx) builder = builder.WithAudioContextSize(ctx).WithSingleSegment();
        return _processors[ctx] = builder.Build();
    }

    /// <summary>Наименьшее окно, в которое целиком помещается кусок (с запасом), иначе whisper обрежет текст.</summary>
    static int CtxFor(int samples)
    {
        int need = (int)Math.Ceiling(samples / (double)SampleRate * CtxPerSecond) + 32;
        return CtxBuckets.FirstOrDefault(b => b >= need, FullCtx);
    }

    public static async Task<Transcriber> CreateAsync(Config cfg, IProgress<string> status)
    {
        var path = Config.Resolve(cfg.WhisperModel);
        if (path == null)
        {
            path = Config.DefaultPath(cfg.WhisperModel);
            status.Report("Скачиваю модель whisper (~550 МБ)…");
            Log.Write($"Модель whisper не найдена, скачиваю в {path}");
            await Config.DownloadAsync(() => WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.LargeV3Turbo, QuantizationType.Q5_0), path);
        }
        var vadPath = Config.Resolve(VadModel);
        if (vadPath == null)
        {
            vadPath = Config.DefaultPath(VadModel);
            await Config.DownloadAsync(() => WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync(SileroVadType.V5_1_2), vadPath);
        }

        status.Report("Загружаю модель whisper…");
        var sw = Stopwatch.StartNew();
        var t = await Task.Run(() => new Transcriber(path, vadPath, cfg));
        Log.Write($"Модель {Path.GetFileName(path)} загружена за {sw.ElapsedMilliseconds} мс, backend: {t.Backend}");
        return t;
    }

    public async Task<List<Utterance>> TranscribeAsync(string wavPath, string speaker, IProgress<string> status, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var audio = await Task.Run(() => ReadWav(wavPath), ct);
        var chunks = SpeechChunks(audio);
        var result = new List<Utterance>();
        for (int i = 0; i < chunks.Count; i++)
        {
            status.Report($"Распознаю: {speaker} {i * 100 / chunks.Count}%");
            var (from, to) = chunks[i];
            int a = (int)(from.TotalSeconds * SampleRate), b = Math.Min(audio.Length, (int)(to.TotalSeconds * SampleRate));
            // Тишина по краям: в начале whisper нужно ≥1 с, в конце — чтобы не съедалось последнее слово.
            var padded = new float[SampleRate + (b - a) + SampleRate / 2];
            audio.AsSpan(a, b - a).CopyTo(padded.AsSpan(SampleRate));
            var lead = TimeSpan.FromSeconds(1);

            await foreach (var seg in Processor(CtxFor(padded.Length)).ProcessAsync(padded, ct))
            {
                var text = DropRepeatedTail(Regex.Replace(seg.Text, @"\s+", " ").Trim());
                if (text.Length == 0 || Hallucination.IsMatch(text)) continue;
                var start = from + seg.Start - lead;
                result.Add(new Utterance(start < from ? from : start, from + seg.End - lead, speaker, text));
            }
        }
        double audioSec = audio.Length / (double)SampleRate;
        Log.Write($"{Path.GetFileName(wavPath)}: {audioSec:F0} с аудио, {chunks.Count} кусков речи, {result.Count} фраз за {sw.Elapsed.TotalSeconds:F1} с");
        return result;
    }

    static readonly Regex SentenceEnd = new(@"(?<=[.!?…])\s+", RegexOptions.Compiled);

    /// <summary>
    /// "А. Б. А. Б." → "А. Б.": с укороченным окном whisper, дойдя до конца речи, начинает кусок заново.
    /// Отрезаются только целые предложения, которые повторяют начало реплики.
    /// </summary>
    static string DropRepeatedTail(string text)
    {
        var sentences = SentenceEnd.Split(text);
        for (int i = 1; i < sentences.Length; i++)
        {
            var tail = string.Join(' ', sentences[i..]);
            if (text.StartsWith(tail, StringComparison.OrdinalIgnoreCase))
                return string.Join(' ', sentences[..i]);
        }
        return text;
    }

    /// <summary>Отрезки речи по VAD, склеенные в куски до ~28 с (одно окно whisper).</summary>
    List<(TimeSpan From, TimeSpan To)> SpeechChunks(float[] audio)
    {
        var chunks = new List<(TimeSpan From, TimeSpan To)>();
        if (audio.Length == 0) return chunks;
        var pad = TimeSpan.FromMilliseconds(250);
        var total = TimeSpan.FromSeconds(audio.Length / (double)SampleRate);
        foreach (var s in _vad.DetectSpeech(audio))
        {
            var from = s.Start - pad < TimeSpan.Zero ? TimeSpan.Zero : s.Start - pad;
            var to = s.End + pad > total ? total : s.End + pad;
            // Длинный монолог без пауз режем по окну whisper.
            for (var f = from; f < to; f += MaxChunk)
            {
                var t = f + MaxChunk < to ? f + MaxChunk : to;
                if (chunks.Count > 0 && f - chunks[^1].To < MaxGap && t - chunks[^1].From <= MaxChunk)
                    chunks[^1] = (chunks[^1].From, t);
                else
                    chunks.Add((f, t));
            }
        }
        return chunks;
    }

    /// <summary>WAV любого формата → 16 кГц моно float.</summary>
    static float[] ReadWav(string path)
    {
        using var reader = new WaveFileReader(path);
        ISampleProvider src = reader.ToSampleProvider();
        if (src.WaveFormat.Channels > 1) src = new DownmixSampleProvider(src);
        if (src.WaveFormat.SampleRate != SampleRate) src = new WdlResamplingSampleProvider(src, SampleRate);
        var result = new List<float>((int)(reader.TotalTime.TotalSeconds * SampleRate) + SampleRate);
        var buf = new float[SampleRate];
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0) result.AddRange(buf.AsSpan(0, n));
        return result.ToArray();
    }

    public void Dispose()
    {
        foreach (var p in _processors.Values) p.Dispose();
        _factory.Dispose();
        _vad.Dispose();
        _vadFactory.Dispose();
    }

    /// <summary>Среднее по каналам. StereoToMono в NAudio умеет только 2 канала, а у микрофонных массивов их бывает 4.</summary>
    sealed class DownmixSampleProvider(ISampleProvider source) : ISampleProvider
    {
        readonly int _channels = source.WaveFormat.Channels;
        float[] _buf = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int need = count * _channels;
            if (_buf.Length < need) _buf = new float[need];
            int read = source.Read(_buf, 0, need) / _channels;
            for (int i = 0; i < read; i++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++) sum += _buf[i * _channels + c];
                buffer[offset + i] = sum / _channels;
            }
            return read;
        }
    }
}
