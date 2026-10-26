using System.Diagnostics;

namespace RecordSummary;

/// <summary>
/// Папка звонка (mic.wav + system.wav) → transcript.md → summary.md.
/// Модели грузятся на время обработки и сразу освобождаются: whisper и LLM вместе не держат видеопамять.
/// </summary>
public static class CallProcessor
{
    static readonly SemaphoreSlim Queue = new(1, 1);   // два звонка подряд обрабатываются по очереди

    /// <summary>Путь к summary.md, или к transcript.md, если саммари выключено или не получилось.</summary>
    public static async Task<string> ProcessAsync(string folder, Config cfg, IProgress<string> status, CancellationToken ct = default)
    {
        await Queue.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var title = Path.GetFileName(folder.TrimEnd('\\', '/'));
            var transcriptPath = Path.Combine(folder, "transcript.md");

            List<Utterance> dialog;
            using (var t = await Transcriber.CreateAsync(cfg, status))
            {
                var mic = await t.TranscribeAsync(Path.Combine(folder, "mic.wav"), Transcript.Me, status, ct);
                var system = await t.TranscribeAsync(Path.Combine(folder, "system.wav"), Transcript.Them, status, ct);
                dialog = Transcript.Merge(mic, system);
            }
            await File.WriteAllTextAsync(transcriptPath, Transcript.ToMarkdown(title, dialog), ct);
            Log.Write($"Транскрипт готов за {sw.Elapsed.TotalSeconds:F0} с: {dialog.Count} реплик");

            if (dialog.Count == 0) return transcriptPath;
            using var llm = await Summarizer.CreateAsync(cfg, status);
            if (llm == null) return transcriptPath;

            sw.Restart();
            var summary = await Summarizer.SummarizeAsync(llm, Transcript.Format(dialog), status, ct);
            var summaryPath = Path.Combine(folder, "summary.md");
            await File.WriteAllTextAsync(summaryPath,
                $"# {title}\n\n{summary}\n\n---\n*Саммари: {llm.Name}. Полный текст — [transcript.md](transcript.md).*\n", ct);
            Log.Write($"Саммари готово за {sw.Elapsed.TotalSeconds:F0} с ({llm.Name})");
            return summaryPath;
        }
        finally { Queue.Release(); }
    }
}
