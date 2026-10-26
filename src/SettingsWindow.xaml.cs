using System.Collections.ObjectModel;
using System.Diagnostics;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using Visibility = System.Windows.Visibility;
using ComboBox = System.Windows.Controls.ComboBox;

namespace RecordSummary;

/// <summary>То, что окну настроек нужно от приложения в трее.</summary>
public interface ISettingsHost
{
    Config Current { get; }
    string Status { get; }
    event Action? StatusChanged;
    bool Autostart { get; set; }
    void Apply(Config cfg);
    /// <summary>Сжать записи, оставшиеся в wav; ход и итог видны в статусе и уведомлении.</summary>
    void CompressExisting();
    /// <summary>Заново распознать и сделать саммари записи; ход виден в статусе и в трее.</summary>
    void Reprocess(string folder);
}

public sealed class AppRow
{
    public string Pattern { get; set; } = "";
    public string Name { get; set; } = "";
}

sealed record Option(string Value, string Label)
{
    public override string ToString() => Label;
}

public partial class SettingsWindow : System.Windows.Window
{
    static readonly Option[] Languages =
    [
        new("ru", "Русский"), new("en", "English"), new("auto", "Определять автоматически"),
        new("uk", "Українська"), new("be", "Беларуская"), new("kk", "Қазақша"),
        new("de", "Deutsch"), new("fr", "Français"), new("es", "Español"),
    ];

    // Оценка "сколько звонка влезает целиком": ~900 символов русской речи в минуту, ~2,5 символа на токен.
    static readonly Option[] Contexts =
    [
        new("8192", "8K — звонок до ~15 мин целиком"),
        new("16384", "16K — до ~35 мин"),
        new("32768", "32K — до ~80 мин"),
    ];

    const string Custom = "";
    static readonly Option[] ApiPresets =
    [
        new("https://openrouter.ai/api/v1", "OpenRouter"),
        new("http://localhost:11434/v1", "Ollama"),
        new("http://localhost:1234/v1", "LM Studio"),
        new(Custom, "Другой адрес"),
    ];

    static readonly string[] OpenRouterModels =
    [
        "anthropic/claude-sonnet-5.5", "anthropic/claude-haiku-5.5", "anthropic/claude-opus-5.5",
        "google/gemini-3.8-flash", "qwen/qwen3.8-flash",
    ];

    readonly ISettingsHost _host;
    readonly ObservableCollection<AppRow> _apps = new();
    bool _presetFromUrl;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        InitializeComponent();
        AppsGrid.ItemsSource = _apps;
        Load(host.Current);

        _host.StatusChanged += OnStatus;
        OnStatus();
        Closed += (_, _) => _host.StatusChanged -= OnStatus;
    }

    // ---------- Загрузка и сбор значений ----------

    void Load(Config c)
    {
        AutoRecordCheck.IsChecked = c.AutoRecord;
        OutputDirBox.Text = c.OutputDir;
        CompressCheck.IsChecked = c.CompressAudio;
        UpdateWavInfo(c.OutputPath);
        AutostartCheck.IsChecked = _host.Autostart;

        StartDelaySlider.Value = c.StartDelaySeconds;
        StopDelaySlider.Value = c.StopDelaySeconds;
        SetApps(c.CallApps);
        RefreshMicUsers();

        FillModels(WhisperModelCombo, "ggml-*.bin", c.WhisperModel);
        var langs = Languages.ToList();
        if (langs.All(l => l.Value != c.Language)) langs.Add(new(c.Language, c.Language));
        Select(LanguageCombo, langs, c.Language);
        var gpus = VulkanDevices.List().Select(d => new Option(d.Index.ToString(), d.ToString())).ToList();
        if (gpus.All(g => g.Value != c.GpuDevice.ToString())) gpus.Add(new(c.GpuDevice.ToString(), $"Vulkan{c.GpuDevice}"));
        Select(GpuCombo, gpus, c.GpuDevice.ToString());
        UseGpuCheck.IsChecked = c.UseGpu;
        UpdateGpuEnabled();

        (c.SummaryProvider.ToLowerInvariant() switch { "api" => ProviderApi, "off" => ProviderOff, _ => ProviderLocal }).IsChecked = true;
        FillModels(LocalModelCombo, "*.gguf", c.LocalModel);
        var contexts = Contexts.ToList();
        if (contexts.All(x => x.Value != c.LocalContextSize.ToString())) contexts.Add(new(c.LocalContextSize.ToString(), $"{c.LocalContextSize} токенов"));
        Select(ContextCombo, contexts, c.LocalContextSize.ToString());

        ApiUrlBox.Text = c.ApiBaseUrl;   // выбирает пресет по адресу
        ApiKeyBox.Password = c.ApiKey;
        ApiModelCombo.ItemsSource = OpenRouterModels;
        ApiModelCombo.Text = c.ApiModel;
        UpdateProvider();
    }

    Config Collect()
    {
        var c = _host.Current.Clone();
        c.AutoRecord = AutoRecordCheck.IsChecked == true;
        c.OutputDir = OutputDirBox.Text.Trim();
        c.CompressAudio = CompressCheck.IsChecked == true;

        c.StartDelaySeconds = (int)StartDelaySlider.Value;
        c.StopDelaySeconds = (int)StopDelaySlider.Value;
        c.CallApps = new();
        foreach (var r in _apps)
        {
            var pattern = r.Pattern.Trim();
            if (pattern.Length > 0) c.CallApps[pattern] = r.Name.Trim().Length > 0 ? r.Name.Trim() : pattern;
        }

        c.WhisperModel = Value(WhisperModelCombo) ?? c.WhisperModel;
        c.Language = Value(LanguageCombo) ?? c.Language;
        c.UseGpu = UseGpuCheck.IsChecked == true;
        c.GpuDevice = int.Parse(Value(GpuCombo) ?? "0");

        c.SummaryProvider = ProviderApi.IsChecked == true ? "api" : ProviderOff.IsChecked == true ? "off" : "local";
        c.LocalModel = Value(LocalModelCombo) ?? c.LocalModel;
        c.LocalContextSize = int.Parse(Value(ContextCombo) ?? c.LocalContextSize.ToString());
        c.ApiBaseUrl = ApiUrlBox.Text.Trim();
        c.ApiKey = ApiKeyBox.Password.Trim();
        c.ApiModel = ApiModelCombo.Text.Trim();
        return c;
    }

    static void Select(ComboBox combo, List<Option> items, string value)
    {
        combo.ItemsSource = items;
        combo.SelectedItem = items.FirstOrDefault(i => i.Value == value) ?? items.FirstOrDefault();
    }

    static string? Value(ComboBox combo) => (combo.SelectedItem as Option)?.Value;

    static void FillModels(ComboBox combo, string pattern, string current)
    {
        var items = Config.FindModels(pattern)
            .Where(m => !m.File.Contains("silero", StringComparison.OrdinalIgnoreCase))   // VAD — не модель распознавания
            .Select(m => new Option(m.Value, $"{Path.GetFileName(m.File)}  ·  {Size(m.File)}"))
            .ToList();
        if (items.All(i => !SameModel(i.Value, current)))
        {
            var path = Config.Resolve(current);
            items.Add(new(current, Path.GetFileName(current) + (path != null ? $"  ·  {Size(path)}" : "  ·  не найден, скачается")));
        }
        combo.ItemsSource = items;
        combo.SelectedItem = items.First(i => SameModel(i.Value, current) || i.Value == current);
    }

    static bool SameModel(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    static string Size(string file)
    {
        double mb = new FileInfo(file).Length / 1048576.0;
        return mb >= 1024 ? $"{mb / 1024:F1} ГБ" : $"{mb:F0} МБ";
    }

    void OnStatus() => Dispatcher.BeginInvoke(() =>
    {
        StatusText.Text = _host.Status;
        AboutStatus.Text = "Состояние: " + _host.Status;
    });

    // ---------- Навигация ----------

    void Nav_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (GeneralPage == null) return;  // вызывается ещё из InitializeComponent
        var tag = (Nav.SelectedItem as System.Windows.Controls.ListBoxItem)?.Tag as string;
        GeneralPage.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        CallsPage.Visibility = tag == "Calls" ? Visibility.Visible : Visibility.Collapsed;
        SpeechPage.Visibility = tag == "Speech" ? Visibility.Visible : Visibility.Collapsed;
        SummaryPage.Visibility = tag == "Summary" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Calls") RefreshMicUsers();
    }

    void Slider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (StartDelayValue != null) StartDelayValue.Text = $"{StartDelaySlider.Value:F0} с";
        if (StopDelayValue != null) StopDelayValue.Text = $"{StopDelaySlider.Value:F0} с";
    }

    void UseGpu_Changed(object sender, RoutedEventArgs e) => UpdateGpuEnabled();

    void UpdateGpuEnabled()
    {
        if (GpuCombo != null) GpuCombo.IsEnabled = UseGpuCheck.IsChecked == true;
    }

    // ---------- Основное ----------

    void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Collect().OutputPath };
        if (dlg.ShowDialog() == true) OutputDirBox.Text = dlg.FolderName;
    }

    void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var dir = Collect().OutputPath;
        Directory.CreateDirectory(dir);
        Shell(dir);
    }

    /// <summary>Сколько места занимают несжатые записи — чтобы было понятно, стоит ли жать "Сжать сейчас".</summary>
    void UpdateWavInfo(string root)
    {
        var folders = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        var sizes = folders.Select(Tracks.WavBytes).Where(b => b > 0).ToList();
        WavInfo.Text = sizes.Count == 0
            ? "Несжатых записей нет."
            : $"Несжатые записи: {sizes.Sum() / 1073741824.0:0.##} ГБ в звонках: {sizes.Count}. Звонок, который сейчас записывается, не трогается.";
        CompressNowButton.IsEnabled = sizes.Count > 0;
    }

    void CompressNow_Click(object sender, RoutedEventArgs e)
    {
        CompressNowButton.IsEnabled = false;
        WavInfo.Text = "Сжимаю… ход виден внизу окна и в трее.";
        _host.CompressExisting();
    }

    // ---------- Звонки ----------

    void SetApps(Dictionary<string, string> apps)
    {
        _apps.Clear();
        foreach (var (pattern, name) in apps) _apps.Add(new AppRow { Pattern = pattern, Name = name });
    }

    void DeleteApps_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in AppsGrid.SelectedItems.OfType<AppRow>().ToList()) _apps.Remove(row);
    }

    void ResetApps_Click(object sender, RoutedEventArgs e) => SetApps(new Config().CallApps);

    void RefreshMicUsers_Click(object sender, RoutedEventArgs e) => RefreshMicUsers();

    void RefreshMicUsers()
    {
        var users = MicUsers.Active().Where(u => !u.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)).ToList();
        MicUsersList.ItemsSource = users;
        if (users.Count > 0) MicUsersList.SelectedIndex = 0;
        MicUsersHint.Text = users.Count == 0 ? "Никто — ни одно приложение сейчас не пишет с микрофона." : "";
    }

    void AddMicUser_Click(object sender, RoutedEventArgs e)
    {
        if (MicUsersList.SelectedItem is not string user) return;
        // Обычная программа: имя exe; пакет Microsoft Store: имя пакета до "_издатель".
        var pattern = user.Contains('\\') ? Path.GetFileName(user) : user.Split('_')[0] + "_";
        if (_apps.Any(a => user.Contains(a.Pattern, StringComparison.OrdinalIgnoreCase)))
        {
            MicUsersHint.Text = "Это приложение уже есть в списке.";
            return;
        }
        var name = Path.GetFileNameWithoutExtension(pattern.TrimEnd('_'));
        _apps.Add(new AppRow { Pattern = pattern, Name = name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : pattern });
        AppsGrid.ScrollIntoView(_apps[^1]);
        MicUsersHint.Text = $"Добавлено: {pattern}. Название можно поправить в таблице.";
    }

    // ---------- Саммари ----------

    void Provider_Changed(object sender, RoutedEventArgs e) => UpdateProvider();

    void UpdateProvider()
    {
        if (LocalPanel == null || ApiPanel == null) return;
        LocalPanel.Visibility = ProviderLocal.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ApiPanel.Visibility = ProviderApi.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void ApiPreset_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_presetFromUrl || ApiPresetCombo.SelectedItem is not Option { Value: var url } || url == Custom) return;
        ApiUrlBox.Text = url;
    }

    void ApiUrl_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ApiPresetCombo == null) return;
        var url = ApiUrlBox.Text.Trim().TrimEnd('/');
        // Пресет следует за адресом, но не перезаписывает его обратно.
        _presetFromUrl = true;
        if (ApiPresetCombo.ItemsSource == null) ApiPresetCombo.ItemsSource = ApiPresets;
        ApiPresetCombo.SelectedItem = ApiPresets.FirstOrDefault(p => p.Value == url) ?? ApiPresets[^1];
        _presetFromUrl = false;

        bool openRouter = url.Contains("openrouter", StringComparison.OrdinalIgnoreCase);
        bool envKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"));
        ApiKeyHint.Text = openRouter
            ? "Хранится в config.json открытым текстом. Пусто — берётся из переменной OPENROUTER_API_KEY" + (envKey ? " (задана)." : " (сейчас не задана).")
            : "Локальным Ollama и LM Studio ключ обычно не нужен.";
    }

    async void LoadModels_Click(object sender, RoutedEventArgs e)
    {
        LoadModelsButton.IsEnabled = false;
        try
        {
            using var api = new ApiLlm(Collect());
            var models = await api.ModelsAsync();
            var current = ApiModelCombo.Text;
            ApiModelCombo.ItemsSource = models;
            ApiModelCombo.Text = current;
            ApiTestResult.Text = $"Моделей на сервере: {models.Count}. Можно начать вводить название — список подскажет.";
        }
        catch (Exception ex) { ApiTestResult.Text = "Список не загрузился: " + ex.Message; }
        finally { LoadModelsButton.IsEnabled = true; }
    }

    async void ApiTest_Click(object sender, RoutedEventArgs e)
    {
        ApiTestButton.IsEnabled = false;
        ApiTestResult.Text = "Отправляю…";
        try
        {
            using var api = new ApiLlm(Collect());
            var sw = Stopwatch.StartNew();
            var answer = await api.CompleteAsync("Это проверка связи.", "Ответь одним словом: работает", 20);
            ApiTestResult.Text = $"✓ {api.Name} ответила за {sw.Elapsed.TotalSeconds:F1} с: «{answer.Trim()}»";
        }
        catch (Exception ex) { ApiTestResult.Text = "✗ " + ex.Message; }
        finally { ApiTestButton.IsEnabled = true; }
    }

    void Reprocess_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _host.Current.OutputPath, Title = "Папка звонка" };
        if (dlg.ShowDialog() != true) return;
        if (Tracks.Find(dlg.FolderName, Tracks.Mic) == null || Tracks.Find(dlg.FolderName, Tracks.System) == null)
        {
            StatusText.Text = "В этой папке нет дорожек mic и system — выберите папку звонка.";
            return;
        }
        _host.Reprocess(dlg.FolderName);
    }

    // ---------- Файлы моделей ----------

    void BrowseWhisper_Click(object sender, RoutedEventArgs e) => Browse(WhisperModelCombo, "Модели whisper (*.bin)|*.bin");

    void BrowseLocal_Click(object sender, RoutedEventArgs e) => Browse(LocalModelCombo, "Модели GGUF (*.gguf)|*.gguf");

    static void Browse(ComboBox combo, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter };
        if (dlg.ShowDialog() != true) return;
        var items = ((List<Option>)combo.ItemsSource).ToList();
        var item = new Option(dlg.FileName, $"{Path.GetFileName(dlg.FileName)}  ·  {Size(dlg.FileName)}");
        items.Add(item);
        combo.ItemsSource = items;
        combo.SelectedItem = item;
    }

    void OpenConfig_Click(object sender, RoutedEventArgs e) => Shell(Path.Combine(Config.BaseDir, "config.json"));

    void OpenLog_Click(object sender, RoutedEventArgs e) => Shell(Log.FilePath);

    void OpenModels_Click(object sender, RoutedEventArgs e)
    {
        var dir = Config.FindModels("*").Select(m => Path.GetDirectoryName(m.File)).FirstOrDefault()
                  ?? Path.Combine(Config.BaseDir, "models");
        Directory.CreateDirectory(dir!);
        Shell(dir!);
    }

    static void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    // ---------- Кнопки ----------

    void Apply_Click(object sender, RoutedEventArgs e) => Apply();

    // IsCancel сам закрывает только диалог (ShowDialog), а окно настроек немодальное.
    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Apply()) Close();
    }

    bool Apply()
    {
        try
        {
            _host.Autostart = AutostartCheck.IsChecked == true;
            _host.Apply(Collect());
            StatusText.Text = "Настройки сохранены";
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Настройки не применены: {ex}");
            StatusText.Text = "Не удалось применить: " + ex.Message;
            return false;
        }
    }
}
