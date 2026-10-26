using System.Text;

namespace RecordSummary;

/// <summary>Сводит фразы обеих дорожек в один диалог по времени.</summary>
public static class Transcript
{
    public const string Me = "Я", Them = "Собеседник";

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
