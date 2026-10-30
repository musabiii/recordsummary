using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RecordSummary;

/// <summary>Сводит фразы обеих дорожек в один диалог по времени.</summary>
public static class Transcript
{
    public const string Me = "Я", Them = "Собеседник";

    /// <summary>Реплики с началом и концом — для окна записей (карта разговора, перемотка по клику).</summary>
    public const string JsonFile = "transcript.json";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task SaveJsonAsync(string path, List<Utterance> utterances, CancellationToken ct = default) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(utterances, Json), ct);

    /// <summary>transcript.json, а для записей, обработанных до его появления, — разбор transcript.md.</summary>
    public static List<Utterance> Load(string folder)
    {
        var json = Path.Combine(folder, JsonFile);
        if (File.Exists(json))
            return JsonSerializer.Deserialize<List<Utterance>>(File.ReadAllText(json), Json) ?? [];

        var md = Path.Combine(folder, "transcript.md");
        if (!File.Exists(md)) return [];
        var result = new List<Utterance>();
        foreach (Match m in MdLine.Matches(File.ReadAllText(md)))
        {
            var start = TimeSpan.Parse(m.Groups[1].Value);
            var text = m.Groups[3].Value.Trim();
            // Конца реплики в md нет: оцениваем по длине (~15 символов в секунду), не дальше начала следующей.
            var end = start + TimeSpan.FromSeconds(Math.Max(1, text.Length / 15.0));
            if (result.Count > 0 && result[^1].End > start) result[^1] = result[^1] with { End = start };
            result.Add(new Utterance(start, end, m.Groups[2].Value, text));
        }
        return result;
    }

    static readonly Regex MdLine = new(@"^\*\*\[(\d{2}:\d{2}:\d{2})\] (.+?):\*\* (.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Подряд идущие фразы одного говорящего с паузой меньше этой склеиваются в одну реплику.</summary>
    static readonly TimeSpan JoinGap = TimeSpan.FromSeconds(2);

    public static List<Utterance> Merge(IEnumerable<Utterance> mic, IEnumerable<Utterance> system)
    {
        var result = new List<Utterance>();
        foreach (var u in mic.Concat(system).OrderBy(u => u.Start))
        {
            if (result.Count > 0 && result[^1].Speaker == u.Speaker && u.Start - result[^1].End < JoinGap)
                result[^1] = result[^1] with { End = u.End, Text = result[^1].Text + " " + u.Text };
            else
                result.Add(u);
        }
        return result;
    }

    /// <summary>
    /// Номера собеседников по порядку их реплик: у кого речь не распозналась (короткое "угу"), номер не занимает.
    /// Остался один — он просто "Собеседник".
    /// </summary>
    public static List<Utterance> Renumber(List<Utterance> utterances)
    {
        var them = utterances.Select(u => u.Speaker).Where(s => s.StartsWith(Them + " ")).Distinct().ToList();
        return utterances
            .Select(u => them.Contains(u.Speaker) ? u with { Speaker = them.Count > 1 ? $"{Them} {them.IndexOf(u.Speaker) + 1}" : Them } : u)
            .ToList();
    }

    /// <summary>Текст для LLM и для человека: "[00:01:23] Я: ...".</summary>
    public static string Format(IEnumerable<Utterance> utterances)
    {
        var sb = new StringBuilder();
        foreach (var u in utterances)
            sb.Append('[').Append(u.Start.ToString(@"hh\:mm\:ss")).Append("] ").Append(u.Speaker).Append(": ").AppendLine(u.Text);
        return sb.ToString();
    }

    public static string ToMarkdown(string title, IEnumerable<Utterance> utterances)
    {
        var sb = new StringBuilder();
        sb.Append("# Транскрипт: ").AppendLine(title).AppendLine();
        foreach (var u in utterances)
            sb.Append("**[").Append(u.Start.ToString(@"hh\:mm\:ss")).Append("] ").Append(u.Speaker).Append(":** ").AppendLine(u.Text).AppendLine();
        return sb.ToString();
    }
}
