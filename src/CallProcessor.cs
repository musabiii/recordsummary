using System.Diagnostics;

namespace RecordSummary;

/// <summary>
/// Папка звонка (mic.wav + system.wav) → transcript.md + transcript.json → summary.md.
/// Модели грузятся на время обработки и сразу освобождаются: whisper и LLM вместе не держат видеопамять.
/// Если обработка упала, в папке остаётся error.txt — по нему окно записей показывает ошибку.
/// </summary>
public static class CallProcessor
{
    public const string ErrorFile = "error.txt";

    static readonly SemaphoreSlim Queue = new(1, 1);   // два звонка подряд обрабатываются по очереди

    /// <summary>Путь к summary.md, или к transcript.md, если саммари выключено или не получилось.</summary>
    public static async Task<string> ProcessAsync(string folder, Config cfg, IProgress<string> status, CancellationToken ct = default)
    {
        await Queue.WaitAsync(ct);
        var errorPath = Path.Combine(folder, ErrorFile);
        try
        {
            File.Delete(errorPath);
            return await RunAsync(folder, cfg, status, ct);
        }
        catch (Exception e)
        {
            try { await File.WriteAllTextAsync(errorPath, e.Message, CancellationToken.None); } catch { }
            throw;
        }
        finally { Queue.Release(); }
    }

    static async Task<string> RunAsync(string folder, Config cfg, IProgress<string> status, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var title = Path.GetFileName(folder.TrimEnd('\\', '/'));
        var transcriptPath = Path.Combine(folder, "transcript.md");

        List<Utterance> dialog;
        using (var t = await Transcriber.CreateAsync(cfg, status))
        {
            var mic = await t.TranscribeAsync(Tracks.Require(folder, Tracks.Mic), Transcript.Me, status, ct);
            var system = await t.TranscribeAsync(Tracks.Require(folder, Tracks.System), Transcript.Them, status, ct);
            dialog = Transcript.Merge(mic, system);
        }
        await File.WriteAllTextAsync(transcriptPath, Transcript.ToMarkdown(title, dialog), ct);
        await Transcript.SaveJsonAsync(Path.Combine(folder, Transcript.JsonFile), dialog, ct);
        Log.Write($"Транскрипт готов за {sw.Elapsed.TotalSeconds:F0} с: {dialog.Count} реплик");

        // Сжимаем сразу после распознавания: даже если саммари не получится, место уже освобождено.
        if (cfg.CompressAudio) CompressQuietly(folder, status);

        if (dialog.Count == 0) return transcriptPath;
        using var llm = await Summarizer.CreateAsync(cfg, status);
        if (llm == null) return transcriptPath;

        sw.Restart();
        var summary = await Summarizer.SummarizeAsync(llm, Transcript.Format(dialog), status, ct);
        // Тему модель пишет первой строкой "# …"; если забыла — заголовок по папке.
        if (!summary.StartsWith("# ")) summary = $"# {title}\n\n{summary}";
        var summaryPath = Path.Combine(folder, "summary.md");
        await File.WriteAllTextAsync(summaryPath,
            $"{summary}\n\n---\n*Саммари: {llm.Name}. Полный текст — [transcript.md](transcript.md).*\n", ct);
        Log.Write($"Саммари готово за {sw.Elapsed.TotalSeconds:F0} с ({llm.Name})");
        return summaryPath;
    }

    /// <summary>Сжатие не должно ронять обработку: не вышло — остаётся wav, звонок всё равно доступен.</summary>
    static void CompressQuietly(string folder, IProgress<string> status)
    {
        if (Tracks.WavBytes(folder) == 0) return;
        status.Report("Сжимаю запись…");
        var sw = Stopwatch.StartNew();
        try
        {
            long saved = Compressor.Compress(folder);
            Log.Write($"Запись сжата за {sw.Elapsed.TotalSeconds:F1} с, освобождено {saved / 1048576.0:F0} МБ");
        }
        catch (Exception e) { Log.Write($"Не удалось сжать {folder}: {e}"); }
    }

    /// <summary>
    /// Сжать записи, которые остались в wav (сделанные до включения сжатия или с выключенным).
    /// Через ту же очередь, что и обработка, чтобы не трогать звонок, который сейчас распознаётся.
    /// </summary>
    public static async Task<(int Calls, long Saved)> CompressAllAsync(IEnumerable<string> folders, IProgress<string> status, CancellationToken ct = default)
    {
        var list = folders.Where(f => Tracks.WavBytes(f) > 0).ToList();
        int done = 0;
        long saved = 0;
        foreach (var folder in list)
        {
            await Queue.WaitAsync(ct);
            try
            {
                status.Report($"Сжимаю записи: {done + 1} из {list.Count}");
                saved += await Task.Run(() => Compressor.Compress(folder), ct);
                done++;
            }
            catch (Exception e) { Log.Write($"Не удалось сжать {folder}: {e.Message}"); }
            finally { Queue.Release(); }
        }
        Log.Write($"Сжато звонков: {done} из {list.Count}, освобождено {saved / 1048576.0:F0} МБ");
        return (done, saved);
    }
}
