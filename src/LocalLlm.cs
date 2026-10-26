using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace RecordSummary;

/// <summary>
/// Локальная gguf-модель через LLamaSharp (llama.cpp, Vulkan). Чат-шаблон берётся из самой модели,
/// так что подходит любая instruct-модель: Qwen, Llama, Gemma, Mistral.
/// </summary>
public sealed class LocalLlm : ILlm
{
    // Грубая оценка для русского текста; точное число токенов проверяется перед генерацией.
    const double CharsPerToken = 2.5;
    const int PromptReserve = 600;   // системный промпт и разметка шаблона

    readonly LLamaWeights _weights;
    readonly LLamaContext _context;
    readonly LLamaBatch _batch = new();
    readonly int _contextSize;

    public string Name { get; }
    public int MaxInputChars => (int)((_contextSize - PromptReserve - 2000) * CharsPerToken);

    LocalLlm(string path, Config cfg)
    {
        _contextSize = cfg.LocalContextSize;
        var p = new ModelParams(path)
        {
            ContextSize = (uint)cfg.LocalContextSize,
            GpuLayerCount = cfg.UseGpu ? cfg.LocalGpuLayers : 0,
            SplitMode = GPUSplitMode.None,   // целиком на одной карте, а не вразброс по встроенной и дискретной
        };
        if (cfg.UseGpu) p.Devices.Add($"Vulkan{cfg.GpuDevice}");
        _weights = LLamaWeights.LoadFromFile(p);
        _context = _weights.CreateContext(p);
        Name = Path.GetFileNameWithoutExtension(path);
    }

    static bool _logConfigured;

    public static async Task<LocalLlm> CreateAsync(Config cfg, IProgress<string> status)
    {
        // Настраивается только до первой загрузки нативной библиотеки.
        if (!_logConfigured)
        {
            _logConfigured = true;
            NativeLibraryConfig.All.WithLogCallback((level, message) =>
            {
                if (level == LLamaLogLevel.Error || message.StartsWith("ggml_vulkan: ") || message.Contains("offloaded"))
                    Log.Write("llama: " + message.TrimEnd());
            });
        }

        var path = Config.Resolve(cfg.LocalModel);
        if (path == null)
        {
            if (string.IsNullOrWhiteSpace(cfg.LocalModelUrl))
                throw new FileNotFoundException($"Локальная модель не найдена: {cfg.LocalModel}");
            path = Config.DefaultPath(cfg.LocalModel);
            status.Report("Скачиваю модель для саммари…");
            Log.Write($"Модель для саммари не найдена, скачиваю {cfg.LocalModelUrl} в {path}");
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            await Config.DownloadAsync(() => http.GetStreamAsync(cfg.LocalModelUrl), path);
        }

        status.Report("Загружаю модель для саммари…");
        var sw = Stopwatch.StartNew();
        var llm = await Task.Run(() => new LocalLlm(path, cfg));
        Log.Write($"Модель {llm.Name} загружена за {sw.ElapsedMilliseconds} мс");
        return llm;
    }

    public async Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct = default)
    {
        var template = new LLamaTemplate(_weights) { AddAssistant = true };
        template.Add("system", system);
        template.Add("user", user);
        var prompt = Encoding.UTF8.GetString(template.Apply());

        var tokens = _context.Tokenize(prompt, addBos: true, special: true);
        if (tokens.Length + maxTokens > _contextSize)
            throw new InvalidOperationException($"Промпт {tokens.Length} токенов + ответ {maxTokens} не влезают в контекст {_contextSize}");

        var sw = Stopwatch.StartNew();
        _context.NativeHandle.MemorySequenceRemove((LLamaSeqId)0, 0, -1);   // запросы независимы
        await DecodeAsync(tokens, ct);
        var prefill = sw.Elapsed;

        using var sampler = new DefaultSamplingPipeline { Temperature = 0.3f, RepeatPenalty = 1.1f };
        var decoder = new StreamingTokenDecoder(_context);
        var vocab = _weights.Vocab;
        int pos = tokens.Length, generated = 0;
        for (; generated < maxTokens; generated++)
        {
            ct.ThrowIfCancellationRequested();
            var token = sampler.Sample(_context.NativeHandle, _batch.TokenCount - 1);
            if (token == vocab.EOS || token == vocab.EOT) break;
            decoder.Add(token);
            _batch.Clear();
            _batch.Add(token, pos++, (LLamaSeqId)0, true);
            var r = await _context.DecodeAsync(_batch, ct);
            if (r != DecodeResult.Ok) throw new InvalidOperationException($"llama decode: {r}");
        }
        Log.Write($"LLM {Name}: {tokens.Length} токенов промпта за {prefill.TotalSeconds:F1} с, " +
                  $"{generated} токенов ответа за {(sw.Elapsed - prefill).TotalSeconds:F1} с");
        return decoder.Read();
    }

    /// <summary>Прогоняет токены через модель пачками; логиты нужны только для последнего.</summary>
    async Task DecodeAsync(LLamaToken[] tokens, CancellationToken ct)
    {
        int batchSize = (int)_context.BatchSize;
        for (int i = 0; i < tokens.Length; i += batchSize)
        {
            _batch.Clear();
            int end = Math.Min(tokens.Length, i + batchSize);
            for (int j = i; j < end; j++)
                _batch.Add(tokens[j], j, (LLamaSeqId)0, j == tokens.Length - 1);
            var r = await _context.DecodeAsync(_batch, ct);
            if (r != DecodeResult.Ok) throw new InvalidOperationException($"llama decode: {r}");
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        _weights.Dispose();
    }
}
