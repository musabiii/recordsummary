using System.Text.Encodings.Web;
using System.Text.Json;

namespace RecordSummary;

public sealed class Config
{
    public static readonly string BaseDir = AppContext.BaseDirectory;
    static readonly string FilePath = Path.Combine(BaseDir, "config.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>true — начинать запись сразу; false — спрашивать уведомлением.</summary>
    public bool AutoRecord { get; set; }

    /// <summary>Сколько приложение должно держать микрофон, чтобы считать это звонком (отсекает голосовой поиск и т.п.).</summary>
    public int StartDelaySeconds { get; set; } = 3;

    /// <summary>Сколько микрофон должен быть свободен, чтобы считать звонок законченным.</summary>
    public int StopDelaySeconds { get; set; } = 5;

    public int PollMs { get; set; } = 1000;

    /// <summary>Папка для записей; пусто — Документы\RecordSummary.</summary>
    public string OutputDir { get; set; } = "";

    /// <summary>
    /// Фрагмент имени exe или пакета → название для уведомлений.
    /// Обычные программы видны по пути к exe, UWP/MSIX (новый Teams, WhatsApp) — по имени пакета.
    /// </summary>
    public Dictionary<string, string> CallApps { get; set; } = new()
    {
        ["zoom.exe"] = "Zoom",
        ["ms-teams.exe"] = "Teams",
        ["MSTeams_"] = "Teams",
        ["teams.exe"] = "Teams",
        ["telegram.exe"] = "Telegram",
        ["discord.exe"] = "Discord",
        ["slack.exe"] = "Slack",
        ["WhatsApp"] = "WhatsApp",
        ["skype"] = "Skype",
        ["chrome.exe"] = "Chrome",
        ["msedge.exe"] = "Edge",
        ["firefox.exe"] = "Firefox",
        ["Yandex\\YandexBrowser"] = "Яндекс Браузер",
    };

    // --- Транскрипция (whisper.cpp) ---

    /// <summary>Путь к ggml-модели whisper. Относительный путь ищется рядом с exe и выше по дереву папок.</summary>
    public string WhisperModel { get; set; } = "models/ggml-large-v3-turbo-q5_0.bin";

    /// <summary>Код языка ("ru", "en", ...) или "auto".</summary>
    public string Language { get; set; } = "ru";

    public bool UseGpu { get; set; } = true;

    /// <summary>Номер Vulkan-устройства (если видеокарт несколько, например встроенная + дискретная).</summary>
    public int GpuDevice { get; set; } = 0;

    // --- Саммари ---

    /// <summary>"local" — gguf-модель внутри приложения, "api" — OpenAI-совместимый API (OpenRouter, Ollama, LM Studio), "off" — только транскрипт.</summary>
    public string SummaryProvider { get; set; } = "local";

    public string LocalModel { get; set; } = "models/Qwen2.5-7B-Instruct-Q4_K_M.gguf";

    /// <summary>Откуда скачать локальную модель, если её нет. Пусто — не скачивать.</summary>
    public string LocalModelUrl { get; set; } = "https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q4_K_M.gguf";

    /// <summary>Контекст локальной модели в токенах. Длинный звонок режется на куски под него.</summary>
    public int LocalContextSize { get; set; } = 16384;

    /// <summary>Сколько слоёв держать на видеокарте (99 = все). Уменьшите, если не хватает видеопамяти.</summary>
    public int LocalGpuLayers { get; set; } = 99;

    /// <summary>OpenRouter: https://openrouter.ai/api/v1; Ollama: http://localhost:11434/v1; LM Studio: http://localhost:1234/v1.</summary>
    public string ApiBaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    /// <summary>Ключ API; пусто — берётся из переменной окружения OPENROUTER_API_KEY.</summary>
    public string ApiKey { get; set; } = "";

    public string ApiModel { get; set; } = "anthropic/claude-sonnet-5.5";

    /// <summary>Сколько символов транскрипта отправлять в API за раз; длиннее — режется на куски.</summary>
    public int ApiMaxInputChars { get; set; } = 400_000;

    public string OutputPath => string.IsNullOrWhiteSpace(OutputDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RecordSummary")
        : OutputDir;

    public static Config Load()
    {
        Config cfg;
        try
        {
            cfg = File.Exists(FilePath) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Json) ?? new() : new();
        }
        catch (Exception e)
        {
            Log.Write($"config.json не прочитан, настройки по умолчанию: {e.Message}");
            return new();
        }
        cfg.Save();   // чтобы в файле появились новые настройки
        return cfg;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); }
        catch (Exception e) { Log.Write($"config.json не сохранён: {e.Message}"); }
    }

    /// <summary>Находит файл модели: абсолютный путь, рядом с exe, или в родительских папках (удобно при запуске из bin/).</summary>
    public static string? Resolve(string model)
    {
        if (Path.IsPathRooted(model)) return File.Exists(model) ? model : null;
        for (var dir = new DirectoryInfo(BaseDir); dir != null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, model);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public Config Clone() => JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(this, Json), Json)!;

    /// <summary>Модели в папках models/ рядом с exe и выше по дереву, в виде "models/имя" (так их находит Resolve).</summary>
    public static IEnumerable<(string Value, string File)> FindModels(string pattern)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var dir = new DirectoryInfo(BaseDir); dir != null; dir = dir.Parent)
        {
            var models = new DirectoryInfo(Path.Combine(dir.FullName, "models"));
            if (!models.Exists) continue;
            foreach (var f in models.EnumerateFiles(pattern).OrderBy(f => f.Name))
                if (seen.Add(f.Name)) yield return ("models/" + f.Name, f.FullName);
        }
    }

    public static string DefaultPath(string model) => Path.IsPathRooted(model) ? model : Path.Combine(BaseDir, model);

    /// <summary>Скачивает файл через .part, чтобы оборванная загрузка не выглядела готовой моделью.</summary>
    public static async Task DownloadAsync(Func<Task<Stream>> open, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".part";
        await using (var src = await open())
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst);
        File.Move(tmp, path, true);
    }
}

public static class Log
{
    static readonly object Gate = new();
    public static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "recordsummary.log");

    public static void Write(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
        }
        Console.WriteLine(line);
    }
}
