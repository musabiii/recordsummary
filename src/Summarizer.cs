using System.Text;
using System.Text.RegularExpressions;

namespace RecordSummary;

public interface ILlm : IDisposable
{
    string Name { get; }

    /// <summary>Сколько символов транскрипта влезает в один запрос вместе с промптом и ответом.</summary>
    int MaxInputChars { get; }

    Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct = default);
}

/// <summary>
/// Саммари звонка. Если транскрипт не влезает в контекст модели (локальные модели, долгие звонки),
/// сначала по кускам пишутся заметки, а итог собирается уже из заметок.
/// </summary>
public static class Summarizer
{
    const string SummarySystem =
        "Ты делаешь саммари рабочего звонка по транскрипту. В транскрипте \"Я\" — владелец записи, " +
        "\"Собеседник\" — все остальные участники (их может быть несколько, имена бери из разговора, если они звучат). " +
        "Транскрипт распознан автоматически, в нём бывают ошибки — восстанавливай смысл по контексту, но не выдумывай. " +
        "Пиши по-русски, в Markdown, строго в таком формате:\n\n" +
        "## Кратко\n2–4 предложения: о чём звонок и чем закончился.\n\n" +
        "## Ключевые моменты\nМаркированный список главного, что обсуждали.\n\n" +
        "## Решения\nЧто договорились. Если решений не было — \"Нет\".\n\n" +
        "## Задачи\nСписок \"- [ ] кто — что — срок\" (срок, только если назван). Если задач нет — \"Нет\".\n\n" +
        "## Открытые вопросы\nЧто осталось нерешённым. Если всё решено — \"Нет\".\n\n" +
        "Не добавляй ничего до первого заголовка и после последнего раздела.";

    const string NotesSystem =
        "Это фрагмент транскрипта длинного рабочего звонка. \"Я\" — владелец записи, \"Собеседник\" — остальные. " +
        "Выпиши по-русски сжатые заметки по фрагменту: темы, факты и цифры, решения, задачи (кто — что — срок), открытые вопросы. " +
        "Только то, что есть во фрагменте, без вступлений и выводов.";

    static readonly Regex Think = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.Compiled);

    public static async Task<string> SummarizeAsync(ILlm llm, string transcript, IProgress<string> status, CancellationToken ct = default)
    {
        var text = transcript;
        // Сжимаем, пока не влезет: заметки по кускам, затем, если нужно, заметки по заметкам.
        for (int round = 1; text.Length > llm.MaxInputChars; round++)
        {
            var chunks = Split(text, llm.MaxInputChars);
            var notes = new StringBuilder();
            for (int i = 0; i < chunks.Count; i++)
            {
                status.Report($"Саммари: фрагмент {i + 1}/{chunks.Count}");
                notes.AppendLine($"### Фрагмент {i + 1}").AppendLine(Clean(await llm.CompleteAsync(NotesSystem, chunks[i], 800, ct))).AppendLine();
            }
            Log.Write($"Саммари, проход {round}: {text.Length} символов → {chunks.Count} фрагментов → {notes.Length} символов заметок");
            if (notes.Length >= text.Length) throw new InvalidOperationException("Заметки не короче исходного текста — модель не сжимает");
            text = notes.ToString();
        }
        status.Report("Саммари: итог");
        var input = text == transcript ? "Транскрипт:\n\n" + text : "Заметки по фрагментам звонка (по порядку):\n\n" + text;
        return Clean(await llm.CompleteAsync(SummarySystem, input, 2000, ct));
    }

    /// <summary>Режет по строкам (репликам), чтобы не рвать фразы посередине.</summary>
    static List<string> Split(string text, int maxChars)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            // Одна реплика длиннее куска (долгий монолог) режется сама.
            for (int i = 0; i < Math.Max(1, line.Length); i += maxChars - 1)
            {
                var piece = line.AsSpan(i, Math.Min(maxChars - 1, line.Length - i));
                if (sb.Length > 0 && sb.Length + piece.Length + 1 > maxChars) { chunks.Add(sb.ToString()); sb.Clear(); }
                sb.Append(piece).Append('\n');
            }
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }

    /// <summary>Рассуждающие модели (Qwen3, DeepSeek-R1) пишут ход мыслей в &lt;think&gt; — в саммари он не нужен.</summary>
    static string Clean(string s) => Think.Replace(s, "").Trim();

    public static async Task<ILlm?> CreateAsync(Config cfg, IProgress<string> status) => cfg.SummaryProvider.ToLowerInvariant() switch
    {
        "off" or "" => null,
        "api" => new ApiLlm(cfg),
        "local" => await LocalLlm.CreateAsync(cfg, status),
        var p => throw new InvalidOperationException($"Неизвестный SummaryProvider \"{p}\": нужно local, api или off"),
    };
}
