namespace RecordSummary;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Проверка детектора без трея: RecordSummary.exe --detect — кто сейчас держит микрофон (в лог).
        if (args.Length == 1 && args[0] == "--detect")
            return Detect();
        // Проверка записи без детектора: RecordSummary.exe --record 10 [папка]
        if (args.Length >= 2 && args[0] == "--record")
            return Record(int.Parse(args[1]), args.ElementAtOrDefault(2));
        // Транскрипт и саммари готовой записи: RecordSummary.exe --process "папка звонка"
        if (args.Length == 2 && args[0] == "--process")
            return Process(args[1]).GetAwaiter().GetResult();

        using var mutex = new Mutex(true, @"Local\RecordSummary.SingleInstance" + TrayApp.InstanceSuffix, out bool first);
        if (!first)
        {
            // Уже запущено: повторный запуск открывает окно записей работающего экземпляра.
            try { using var show = EventWaitHandle.OpenExisting(TrayApp.ShowWindowEvent); show.Set(); } catch { }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
        return 0;
    }

    static int Detect()
    {
        var cfg = Config.Load();
        var users = MicUsers.Active();
        Log.Write(users.Count == 0 ? "Микрофон никто не держит" : "Микрофон держат: " + string.Join("; ", users));
        Log.Write($"Звонок: {CallDetector.FindCallApp(cfg) ?? "нет"}");
        return 0;
    }

    static async Task<int> Process(string folder)
    {
        try
        {
            var result = await CallProcessor.ProcessAsync(folder, Config.Load(), new Progress<string>(Log.Write));
            Log.Write($"Готово: {result}");
            return 0;
        }
        catch (Exception e)
        {
            Log.Write($"Ошибка обработки: {e}");
            return 1;
        }
    }

    static int Record(int seconds, string? folder)
    {
        folder ??= Path.Combine(Config.Load().OutputPath, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} test");
        using (new CallRecorder(folder))
            Thread.Sleep(seconds * 1000);
        foreach (var f in Directory.GetFiles(folder, "*.wav"))
        {
            using var r = new NAudio.Wave.WaveFileReader(f);
            Log.Write($"{Path.GetFileName(f)}: {r.TotalTime.TotalSeconds:F2} с, {r.WaveFormat}");
        }
        return 0;
    }
}
