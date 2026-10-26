using Microsoft.Win32;

namespace RecordSummary;

/// <summary>
/// Кто сейчас держит микрофон. Windows ведёт этот учёт для индикатора микрофона в трее:
/// у приложения, которое пишет прямо сейчас, LastUsedTimeStop == 0.
/// </summary>
public static class MicUsers
{
    const string Base = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>Путь к exe для обычных программ, имя пакета для UWP/MSIX.</summary>
    public static List<string> Active()
    {
        var result = new List<string>();
        Scan(Base, result);
        Scan(Base + @"\NonPackaged", result);
        return result;
    }

    static void Scan(string path, List<string> result)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        if (key == null) return;
        foreach (var name in key.GetSubKeyNames())
        {
            using var app = key.OpenSubKey(name);
            if (app?.GetValue("LastUsedTimeStop") is long stop && stop == 0
                && app.GetValue("LastUsedTimeStart") is long start && start != 0)
                result.Add(name.Replace('#', '\\'));
        }
    }
}

/// <summary>
/// Опрашивает MicUsers и превращает "приложение для звонков держит микрофон" в события начала и конца звонка
/// с задержками, чтобы короткие захваты микрофона и паузы не дёргали запись.
/// </summary>
public sealed class CallDetector : IDisposable
{
    readonly System.Windows.Forms.Timer _timer;
    Config _cfg;
    string? _candidate;
    DateTime _candidateSince, _lastSeen;

    /// <summary>Название приложения текущего звонка или null.</summary>
    public string? ActiveCall { get; private set; }

    public event Action<string>? CallStarted;
    public event Action<string>? CallEnded;

    public CallDetector(Config cfg)
    {
        _cfg = cfg;
        _timer = new System.Windows.Forms.Timer { Interval = cfg.PollMs };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public Config Config
    {
        get => _cfg;
        set { _cfg = value; _timer.Interval = value.PollMs; }
    }

    /// <summary>Путь к exe (или имя пакета) приложения текущего звонка — по нему видео находит окно звонка.</summary>
    public string? ActiveCallKey { get; private set; }

    /// <summary>Первое приложение для звонков, которое сейчас держит микрофон.</summary>
    public static string? FindCallApp(Config cfg) => FindCall(cfg)?.Name;

    static (string Name, string Key)? FindCall(Config cfg)
    {
        foreach (var user in MicUsers.Active())
            foreach (var (pattern, name) in cfg.CallApps)
                if (user.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return (name, user);
        return null;
    }

    void Tick()
    {
        string? app;
        try
        {
            var call = FindCall(_cfg);
            app = call?.Name;
            if (call != null && (ActiveCall == null || call.Value.Name == ActiveCall)) ActiveCallKey = call.Value.Key;
        }
        catch (Exception e) { Log.Write($"Детектор: {e.Message}"); return; }
        var now = DateTime.UtcNow;

        if (ActiveCall == null)
        {
            if (app == null) { _candidate = null; return; }
            if (app != _candidate) { _candidate = app; _candidateSince = now; }
            if (now - _candidateSince < TimeSpan.FromSeconds(_cfg.StartDelaySeconds)) return;
            ActiveCall = app;
            _lastSeen = now;
            Log.Write($"Звонок начался: {app}");
            CallStarted?.Invoke(app);
        }
        else
        {
            if (app != null) { _lastSeen = now; return; }
            if (now - _lastSeen < TimeSpan.FromSeconds(_cfg.StopDelaySeconds)) return;
            var ended = ActiveCall;
            ActiveCall = null;
            _candidate = null;
            Log.Write($"Звонок закончился: {ended}");
            CallEnded?.Invoke(ended);
        }
    }

    public void Dispose() => _timer.Dispose();
}
