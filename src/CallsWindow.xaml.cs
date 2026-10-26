using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Path = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using TextBox = System.Windows.Controls.TextBox;

namespace RecordSummary;

/// <summary>То, что окну записей нужно от приложения в трее.</summary>
public interface ICallsHost
{
    Config Current { get; }
    /// <summary>Папка звонка, который записывается прямо сейчас.</summary>
    string? RecordingFolder { get; }
    /// <summary>Ход обработки звонка или null, если он сейчас не обрабатывается.</summary>
    string? ProcessingStatus(string folder);
    /// <summary>Началась или закончилась запись или обработка — список надо перечитать.</summary>
    event Action? CallsChanged;
    void Reprocess(string folder);
    void OpenSettings();
}

public partial class CallsWindow : Window
{
    // Цвета говорящих — те же, что на карте разговора; читаются и в светлой, и в тёмной теме.
    static readonly Brush MeBrush = Frozen(0x7F, 0x77, 0xDD), ThemBrush = Frozen(0x1D, 0x9E, 0x75), ShotBrush = Frozen(0xEF, 0x9F, 0x27),
        VideoBrush = Frozen(0x37, 0x8A, 0xDD);
    static readonly double[] Speeds = [1, 1.25, 1.5, 2];
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    readonly ICallsHost _host;
    readonly ObservableCollection<CallRecord> _calls = new();
    readonly ICollectionView _view;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly List<Border> _utteranceViews = [];
    CallRecord? _call;
    SummaryDoc? _summary;
    CallPlayer? _player;
    List<Utterance> _utterances = [];
    List<Screenshot> _shots = [];
    List<VideoSegment> _videos = [];
    VideoSegment? _videoSeg;      // фрагмент, открытый в VideoView
    bool _videoReady, _videoPlaying;
    Rectangle? _playhead;
    int _current = -1, _ticks;
    string _query = "";

    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public CallsWindow(ICallsHost host, string? select = null)
    {
        _host = host;
        InitializeComponent();

        SpeedCombo.ItemsSource = Speeds.Select(s => s.ToString("0.##", Ru) + "×").ToList();
        SpeedCombo.SelectedIndex = 0;

        _view = CollectionViewSource.GetDefaultView(_calls);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CallRecord.DayLabel)));
        _view.Filter = o => _query.Length == 0 || ((CallRecord)o).Matches(_query);
        CallsList.ItemsSource = _view;

        _host.CallsChanged += OnCallsChanged;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) =>
        {
            _host.CallsChanged -= OnCallsChanged;
            _timer.Stop();
            _player?.Dispose();
            CloseVideo();
        };

        Reload(select);
    }

    /// <summary>Закрыть плеер, чтобы освободить файлы (перед сжатием). Откроется снова при следующем обновлении списка.</summary>
    public void ReleasePlayer()
    {
        _player?.Dispose();
        _player = null;
        CloseVideo();
        SetPlayIcon(false);
        PlayerCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>Показать конкретный звонок (из уведомления "Саммари готово").</summary>
    public void Select(string folder) => Reload(folder);

    void OnCallsChanged() => Dispatcher.BeginInvoke(() => Reload(_call?.Folder));

    // ---------- Список ----------

    void Reload(string? select)
    {
        var calls = CallRecord.LoadAll(_host.Current.OutputPath);
        var recording = _host.RecordingFolder;
        foreach (var c in calls)
        {
            if (SameFolder(c.Folder, recording)) c.State = CallState.Recording;
            else if (_host.ProcessingStatus(c.Folder) is { } status) { c.State = CallState.Processing; c.Progress = status; }
        }

        _calls.Clear();
        foreach (var c in calls) _calls.Add(c);
        EmptyListText.Visibility = calls.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyListText.Text = $"Здесь появятся записанные звонки. Записи хранятся в {_host.Current.OutputPath}.";

        var target = calls.FirstOrDefault(c => SameFolder(c.Folder, select)) ?? calls.FirstOrDefault();
        if (target != null && _call != null && SameFolder(target.Folder, _call.Folder) && target.State == _call.State
            && (_player != null || !target.HasAudio))
        {
            // Тот же звонок в том же состоянии: не перезапускаем плеер и не теряем позицию.
            _call = target;
            CallsList.SelectionChanged -= CallsList_SelectionChanged;
            CallsList.SelectedItem = target;
            CallsList.SelectionChanged += CallsList_SelectionChanged;
            return;
        }
        CallsList.SelectedItem = target;
        if (target == null) ShowCall(null);
    }

    static bool SameFolder(string? a, string? b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    void CallsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CallsList.SelectedItem is CallRecord c) ShowCall(c);
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
    }

    // ---------- Выбранный звонок ----------

    void ShowCall(CallRecord? call)
    {
        _player?.Dispose();
        _player = null;
        _call = call;
        _summary = null;
        _utterances = [];
        _utteranceViews.Clear();
        _current = -1;
        _shots = call != null && call.State != CallState.Recording ? Screenshots.List(call.Folder) : [];
        _videos = call != null && call.State != CallState.Recording ? Videos.List(call.Folder) : [];
        CloseVideo();
        SummaryPanel.Children.Clear();
        TranscriptPanel.Children.Clear();
        BuildShots();
        VideoToggle.Visibility = _videos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VideoPanel.Visibility = _videos.Count > 0 && VideoToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PlayerError.Visibility = Visibility.Collapsed;
        SetPlayIcon(false);

        if (call == null)
        {
            TitleText.Text = "Записей пока нет";
            SubtitleText.Text = "";
            SetHeaderButtons(false);
            PlayerCard.Visibility = Tabs.Visibility = Visibility.Collapsed;
            ShowState("Как только начнётся звонок, он появится здесь",
                "Приложение следит за микрофоном: когда его занимает Zoom, Teams, Telegram или другое приложение из списка в настройках, " +
                "предложит записать звонок. После звонка здесь будут запись, транскрипт и саммари.", null, false);
            return;
        }

        TitleText.Text = call.Title;
        ShowSubtitle(call);
        SetHeaderButtons(true);
        ReprocessItem.IsEnabled = call.State is not (CallState.Recording or CallState.Processing);

        PlayerCard.Visibility = call.HasAudio ? Visibility.Visible : Visibility.Collapsed;
        if (call.HasAudio) OpenPlayer(call);
        DrawTimeline();   // без реплик — пустые дорожки, чтобы не остались полосы прошлого звонка

        switch (call.State)
        {
            case CallState.Recording:
                ShowState("Идёт запись", "Звонок ещё не закончился. Когда он завершится, здесь появятся транскрипт и саммари.", null, true);
                return;
            case CallState.Processing:
                ShowState("Обрабатываю звонок", call.Progress, null, true);
                return;
            case CallState.NotProcessed:
                ShowState("Звонок ещё не обработан", "Распознавание и саммари займут несколько минут — их ход виден в списке слева.", "Обработать", false);
                return;
            case CallState.Failed:
                ShowState("Не удалось обработать звонок", call.Error ?? "", "Повторить", false);
                return;
        }

        StatePanel.Visibility = Visibility.Collapsed;
        Tabs.Visibility = Visibility.Visible;
        _utterances = Transcript.Load(call.Folder);
        _summary = SummaryDoc.Load(call.Folder);
        BuildSummary();
        BuildTranscript();
        DrawTimeline();
        Tabs.SelectedIndex = _summary != null || call.Error != null ? 0 : 1;
    }

    void SetHeaderButtons(bool on) => CopyButton.IsEnabled = MoreButton.IsEnabled = on;

    void ShowState(string title, string text, string? button, bool busy)
    {
        Tabs.Visibility = Visibility.Collapsed;
        StatePanel.Visibility = Visibility.Visible;
        StateTitle.Text = title;
        StateText.Text = text;
        StateText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StateProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StateButton.Visibility = button != null ? Visibility.Visible : Visibility.Collapsed;
        StateButton.Content = button;
    }

    void StateButton_Click(object sender, RoutedEventArgs e) => Reprocess_Click(sender, e);

    // ---------- Саммари ----------

    void BuildSummary()
    {
        if (_call?.Error is { } error)
        {
            // Саммари упало (нет кредитов, сервер недоступен), а транскрипт готов: ошибка сверху, остальное как обычно.
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Res("SystemFillColorCriticalBrush") };
            text.Inlines.Add(new Run("Саммари не получилось: " + error + " "));
            var retry = new Hyperlink(new Run("Повторить"));
            retry.Click += (s, e) => Reprocess_Click(s, e);
            text.Inlines.Add(retry);
            SummaryPanel.Children.Add(new Border
            {
                Child = text, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 14), CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1), BorderBrush = Res("SystemFillColorCriticalBrush"), Background = Res("SystemFillColorCriticalBackgroundBrush"),
            });
            if (_summary == null) return;
        }
        if (_summary == null)
        {
            SummaryPanel.Children.Add(new TextBlock
            {
                Text = "Для этого звонка есть только транскрипт: саммари было выключено или не получилось. " +
                       "Включите саммари в настройках и выберите в меню «…» «Заново распознать и сделать саммари».",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("TextFillColorSecondaryBrush"),
            });
            return;
        }

        bool first = true;
        foreach (var line in _summary.Lines)
        {
            switch (line)
            {
                case SummaryHeading h:
                    SummaryPanel.Children.Add(new TextBlock
                    {
                        Text = h.Text, FontSize = 16, FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, first ? 0 : 18, 0, 6),
                    });
                    break;
                case SummaryText t:
                    SummaryPanel.Children.Add(Rich(t.Text, new Thickness(0, 0, 0, 4)));
                    break;
                case SummaryBullet b:
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.Children.Add(new TextBlock { Text = "•", Foreground = Res("TextFillColorSecondaryBrush") });
                    var text = Rich(b.Text, new Thickness(0));
                    Grid.SetColumn(text, 1);
                    row.Children.Add(text);
                    SummaryPanel.Children.Add(row);
                    break;
                case SummaryTask task:
                    SummaryPanel.Children.Add(TaskRow(task));
                    break;
            }
            first = false;
        }
    }

    UIElement TaskRow(SummaryTask task)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = task.Text, TextWrapping = TextWrapping.Wrap };
        var check = new CheckBox { Content = label, IsChecked = task.Done, VerticalContentAlignment = VerticalAlignment.Center };
        void Style(bool done)
        {
            label.TextDecorations = done ? TextDecorations.Strikethrough : null;
            label.Foreground = done ? Res("TextFillColorTertiaryBrush") : Res("TextFillColorPrimaryBrush");
        }
        Style(task.Done);
        // Checked/Unchecked, а не Click: Click не приходит при переключении через экранный диктор и UI Automation.
        void Save(object s, RoutedEventArgs e)
        {
            bool done = check.IsChecked == true;
            Style(done);
            try { _summary!.SetDone(task, done); }
            catch (Exception ex) { Log.Write($"Задача не сохранена: {ex.Message}"); }
        }
        check.Checked += Save;
        check.Unchecked += Save;
        row.Children.Add(check);

        if (task.At is { } at)
        {
            var link = new Hyperlink(new Run("▶ " + Clock(at))) { ToolTip = "Послушать, где об этом договорились" };
            link.Click += (_, _) => Seek(at, play: true);
            var time = new TextBlock(link) { FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(time, 1);
            row.Children.Add(time);
        }
        return row;
    }

    /// <summary>Текст с **жирным** — модели иногда выделяют им имена и сроки.</summary>
    static TextBlock Rich(string text, Thickness margin)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = margin, LineHeight = 21 };
        var parts = Regex.Split(text, @"\*\*(.+?)\*\*");
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) tb.Inlines.Add(new Run(parts[i]) { FontWeight = i % 2 == 1 ? FontWeights.SemiBold : FontWeights.Normal });
        return tb;
    }

    // ---------- Транскрипт ----------

    void BuildTranscript()
    {
        TranscriptPanel.Children.Clear();
        _utteranceViews.Clear();
        _current = -1;
        if (_utterances.Count == 0)
            TranscriptPanel.Children.Add(new TextBlock { Text = "В записи не нашлось речи.", Foreground = Res("TextFillColorSecondaryBrush") });

        // Скриншоты встают между репликами — в тот момент разговора, когда их сделали.
        int shot = 0;
        void ShotsBefore(TimeSpan t)
        {
            for (; shot < _shots.Count && _shots[shot].At <= t; shot++)
                TranscriptPanel.Children.Add(Thumbnail(shot, 280, new Thickness(82, 4, 0, 8)));
        }

        for (int i = 0; i < _utterances.Count; i++)
        {
            var u = _utterances[i];
            ShotsBefore(u.Start);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(new TextBlock
            {
                Text = Clock(u.Start), FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12,
                Foreground = Res("TextFillColorSecondaryBrush"), Margin = new Thickness(0, 2, 0, 0),
            });
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = u.Speaker, FontWeight = FontWeights.SemiBold, Foreground = u.Speaker == Transcript.Me ? MeBrush : ThemBrush });
            body.Children.Add(new TextBlock { Text = u.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), LineHeight = 21 });
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);

            var border = new Border
            {
                Child = grid, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(6),
                Background = System.Windows.Media.Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Слушать с этого места",
            };
            var start = u.Start;
            border.MouseLeftButtonUp += (_, _) => Seek(start, play: true);
            _utteranceViews.Add(border);
            TranscriptPanel.Children.Add(border);
        }
        ShotsBefore(TimeSpan.MaxValue);
    }

    // ---------- Скриншоты ----------

    void BuildShots()
    {
        ShotsPanel.Children.Clear();
        ShotsTab.Visibility = _shots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShotsTab.Header = $"Скриншоты · {_shots.Count}";
        ShotsLabel.Visibility = _shots.Count > 0 || _videos.Count > 0 ? Visibility.Visible : Visibility.Hidden;
        for (int i = 0; i < _shots.Count; i++)
            ShotsPanel.Children.Add(Thumbnail(i, 260, new Thickness(0, 0, 12, 12)));
    }

    /// <summary>Миниатюра со временем снимка; клик открывает просмотр.</summary>
    UIElement Thumbnail(int index, double width, Thickness margin)
    {
        var shot = _shots[index];
        var panel = new StackPanel();
        try
        {
            // Уменьшенная копия в памяти: не держит файл и не грузит 4K-картинки целиком ради превью.
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = (int)(width * 1.5);
            img.UriSource = new Uri(shot.Path);
            img.EndInit();
            img.Freeze();
            panel.Children.Add(new System.Windows.Controls.Image { Source = img, Width = width, Stretch = Stretch.Uniform });
        }
        catch (Exception e) { Log.Write($"Превью скриншота не загрузилось: {e.Message}"); }
        var caption = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), Foreground = Res("TextFillColorSecondaryBrush") };
        caption.Inlines.Add(new Run("  ") { FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") });
        caption.Inlines.Add(new Run(Clock(shot.At)) { FontFamily = new System.Windows.Media.FontFamily("Consolas") });
        panel.Children.Add(caption);

        // Кнопка, а не рамка: открывается и с клавиатуры (Tab, Enter), видна экранному диктору.
        var button = new Button
        {
            Content = panel, Margin = margin, Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Открыть скриншот",
        };
        System.Windows.Automation.AutomationProperties.SetName(button, "Скриншот " + Clock(shot.At));
        button.Click += (_, _) => OpenShot(index);
        return button;
    }

    void OpenShot(int index)
    {
        var viewer = new ScreenshotViewer(_shots.ToList(), index, at => Seek(at, play: true)) { Owner = this };
        viewer.Deleted += RefreshShots;
        viewer.Show();
    }

    /// <summary>После удаления скриншота: перечитать список и всё, где он показан.</summary>
    void RefreshShots()
    {
        if (_call == null) return;
        _shots = Screenshots.List(_call.Folder);
        BuildShots();
        BuildTranscript();
        DrawTimeline();
    }

    // ---------- Плеер ----------

    void OpenPlayer(CallRecord call)
    {
        try
        {
            _player = new CallPlayer(call.Folder) { Tempo = Speeds[Math.Max(0, SpeedCombo.SelectedIndex)] };
            ApplyTracks();
            _player.Stopped += () => Dispatcher.BeginInvoke(() => SetPlayIcon(_player?.IsPlaying == true));
        }
        catch (Exception ex)
        {
            Log.Write($"Плеер: {ex}");
            PlayerError.Text = "Не удалось открыть запись: " + ex.Message;
            PlayerError.Visibility = Visibility.Visible;
        }
        UpdateTime();
    }

    void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    void TogglePlay()
    {
        if (_player == null) return;
        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
        SetPlayIcon(_player.IsPlaying);
    }

    void SetPlayIcon(bool playing) => PlayIcon.Text = playing ? "" : "";

    void Seek(TimeSpan t, bool play)
    {
        if (_player == null) return;
        _player.Position = t;
        if (play && !_player.IsPlaying) { _player.Play(); SetPlayIcon(true); }
        Tick();
    }

    void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_player != null && SpeedCombo.SelectedIndex >= 0) _player.Tempo = Speeds[SpeedCombo.SelectedIndex];
    }

    void Tracks_Checked(object sender, RoutedEventArgs e) => ApplyTracks();

    void ApplyTracks() => _player?.SetTracks(me: TrackMe?.IsChecked == true || TrackBoth?.IsChecked == true,
                                              them: TrackThem?.IsChecked == true || TrackBoth?.IsChecked == true);

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Пробел — пауза/воспроизведение, если не печатаем и не нажимаем кнопку.
        if (e.Key != Key.Space || Keyboard.FocusedElement is TextBox or ButtonBase || _player == null) return;
        TogglePlay();
        e.Handled = true;
    }

    void Tick()
    {
        // Ход обработки в списке слева — раз в полсекунды.
        if (++_ticks % 5 == 0)
            foreach (var c in _calls.Where(c => c.State == CallState.Processing))
                if (_host.ProcessingStatus(c.Folder) is { } status && status != c.Progress)
                {
                    c.Progress = status;
                    c.Notify();
                    if (c == _call) StateText.Text = status;
                }

        if (_player == null) return;
        UpdateTime();
        var pos = _player.Position;
        if (_playhead != null && Timeline.ActualWidth > 0)
            Canvas.SetLeft(_playhead, pos.TotalSeconds / Math.Max(1, _player.Duration.TotalSeconds) * Timeline.ActualWidth - 1);
        SyncVideo(pos, _player.IsPlaying);
        if (!_player.IsPlaying && PlayIcon.Text == "") SetPlayIcon(false);

        // Подсветка текущей реплики в транскрипте.
        int idx = _utterances.FindLastIndex(u => u.Start <= pos + TimeSpan.FromMilliseconds(150));
        if (idx == _current || _utteranceViews.Count != _utterances.Count) return;
        if (_current >= 0) _utteranceViews[_current].Background = System.Windows.Media.Brushes.Transparent;
        _current = idx;
        if (idx < 0) return;
        _utteranceViews[idx].Background = Res("SubtleFillColorSecondaryBrush");
        if (_player.IsPlaying && Tabs.SelectedIndex == 1) _utteranceViews[idx].BringIntoView();
    }

    // ---------- Видео ----------

    /// <summary>
    /// Видео ведёт звук: позиция берётся из плеера звука, видео подтягивается к ней. Вне фрагментов — подсказка.
    /// Частота кадров переменная, и после последнего изменения картинки кадров нет — тогда держим последний кадр.
    /// </summary>
    void SyncVideo(TimeSpan pos, bool playing)
    {
        if (_videos.Count == 0 || VideoPanel.Visibility != Visibility.Visible) return;

        var seg = _videos.FirstOrDefault(v => v.Contains(pos));
        if (seg == null)
        {
            if (_videoSeg != null) CloseVideo();
            VideoView.Visibility = Visibility.Hidden;
            var next = _videos.FirstOrDefault(v => v.Start > pos);
            VideoHint.Text = "В этот момент видео не записывалось" + (next != null ? $"\nследующий фрагмент — с {Clock(next.Start)}" : "");
            return;
        }
        if (seg != _videoSeg)
        {
            CloseVideo();
            _videoSeg = seg;
            VideoView.Source = new Uri(seg.Path);
            VideoView.Pause();   // открыть файл, не запуская; дальше MediaOpened
            VideoHint.Text = "Загрузка видео…";
            return;
        }
        if (!_videoReady) return;

        VideoView.Visibility = Visibility.Visible;
        VideoHint.Text = "";
        var natural = VideoView.NaturalDuration.HasTimeSpan ? VideoView.NaturalDuration.TimeSpan : seg.Duration;
        var target = pos - seg.Start;
        bool beyond = target >= natural;
        if (beyond) target = natural - TimeSpan.FromMilliseconds(50);

        if (playing && !beyond)
        {
            if (Math.Abs(VideoView.SpeedRatio - _player!.Tempo) > 0.01) VideoView.SpeedRatio = _player.Tempo;
            if (!_videoPlaying) { VideoView.Play(); _videoPlaying = true; }
            // Небольшое расхождение не трогаем: частая перемотка видео заметнее, чем 0,3 с рассинхрона.
            if ((VideoView.Position - target).Duration() > TimeSpan.FromMilliseconds(350)) VideoView.Position = target;
        }
        else
        {
            if (_videoPlaying) { VideoView.Pause(); _videoPlaying = false; }
            if ((VideoView.Position - target).Duration() > TimeSpan.FromMilliseconds(100)) VideoView.Position = target;
        }
    }

    void Video_MediaOpened(object sender, RoutedEventArgs e)
    {
        _videoReady = true;
        if (_player != null) SyncVideo(_player.Position, _player.IsPlaying);
    }

    void Video_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Log.Write($"Видео не открылось ({_videoSeg?.Path}): {e.ErrorException?.Message}");
        VideoView.Visibility = Visibility.Hidden;
        VideoHint.Text = "Не удалось открыть видео: " + e.ErrorException?.Message;
    }

    /// <summary>Отпустить файл видео (перед удалением звонка и при смене звонка).</summary>
    void CloseVideo()
    {
        VideoView.Stop();
        VideoView.Close();
        VideoView.Source = null;
        _videoSeg = null;
        _videoReady = _videoPlaying = false;
    }

    // Checked/Unchecked, а не Click: Click не приходит при переключении через экранный диктор и UI Automation.
    void VideoToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (VideoPanel == null) return;   // вызывается ещё из InitializeComponent
        bool show = VideoToggle.IsChecked == true && _videos.Count > 0;
        VideoPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) CloseVideo();
        else if (_player != null) SyncVideo(_player.Position, _player.IsPlaying);
    }

    void VideoPanel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        var seg = _videoSeg ?? (_player != null ? _videos.FirstOrDefault(v => v.Contains(_player.Position)) : null) ?? _videos.FirstOrDefault();
        if (seg != null) Shell(seg.Path);
    }

    void UpdateTime()
    {
        if (_player == null) { TimeText.Text = ""; return; }
        TimeText.Text = $"{Clock(_player.Position)} / {Clock(_player.Duration)}";
    }

    static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    // ---------- Карта разговора ----------

    void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => DrawTimeline();

    void DrawTimeline()
    {
        Timeline.Children.Clear();
        _playhead = null;
        double w = Timeline.ActualWidth, total = (_player?.Duration ?? _call?.Duration ?? TimeSpan.Zero).TotalSeconds;
        if (w <= 0 || total <= 0) return;

        // Сверху строка "Экран" (14 px): фрагменты видео и метки скриншотов; под ней дорожки "Я" и "Собеседник".
        const double meY = 17, themY = 33;
        foreach (var v in _videos)
        {
            var bar = new Rectangle
            {
                Width = Math.Max(3, v.Duration.TotalSeconds / total * w), Height = 6, RadiusX = 2, RadiusY = 2, Fill = VideoBrush,
                ToolTip = $"Видео {Clock(v.Start)}–{Clock(v.End)}",
            };
            Canvas.SetLeft(bar, v.Start.TotalSeconds / total * w);
            Canvas.SetTop(bar, 4);
            Timeline.Children.Add(bar);
        }
        // Подложки дорожек, чтобы было видно, куда кликать, даже на тишине.
        foreach (var y in new[] { meY, themY })
        {
            var lane = new Rectangle { Width = w, Height = 10, RadiusX = 2, RadiusY = 2, Fill = Res("SubtleFillColorSecondaryBrush") };
            Canvas.SetTop(lane, y);
            Timeline.Children.Add(lane);
        }
        foreach (var u in _utterances)
        {
            var r = new Rectangle
            {
                Width = Math.Max(2, (u.End - u.Start).TotalSeconds / total * w), Height = 10, RadiusX = 2, RadiusY = 2,
                Fill = u.Speaker == Transcript.Me ? MeBrush : ThemBrush,
            };
            Canvas.SetLeft(r, u.Start.TotalSeconds / total * w);
            Canvas.SetTop(r, u.Speaker == Transcript.Me ? meY : themY);
            Timeline.Children.Add(r);
        }
        for (int i = 0; i < _shots.Count; i++)
        {
            int index = i;
            var mark = new Rectangle
            {
                Width = 8, Height = 10, RadiusX = 2, RadiusY = 2, Fill = ShotBrush,
                ToolTip = $"Скриншот {Clock(_shots[i].At)} — открыть",
            };
            Canvas.SetLeft(mark, Math.Min(w - 8, _shots[i].At.TotalSeconds / total * w - 4));
            Canvas.SetTop(mark, 2);
            // Клик по метке открывает снимок, а не перематывает (обработчик карты — на Canvas, ниже по всплытию).
            mark.MouseLeftButtonDown += (_, e) => { e.Handled = true; OpenShot(index); };
            Timeline.Children.Add(mark);
        }
        _playhead = new Rectangle { Width = 2, Height = 46, Fill = Res("TextFillColorPrimaryBrush"), IsHitTestVisible = false };
        Timeline.Children.Add(_playhead);
        Tick();
    }

    TimeSpan TimelineTime(MouseEventArgs e)
    {
        double total = (_player?.Duration ?? TimeSpan.Zero).TotalSeconds;
        double x = Math.Clamp(e.GetPosition(Timeline).X / Math.Max(1, Timeline.ActualWidth), 0, 1);
        return TimeSpan.FromSeconds(total * x);
    }

    void Timeline_MouseDown(object sender, MouseButtonEventArgs e) => Seek(TimelineTime(e), play: false);

    void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        var t = TimelineTime(e);
        if (e.OriginalSource is Rectangle { ToolTip: not null }) return;   // над меткой скриншота — её собственная подсказка
        var u = _utterances.LastOrDefault(x => x.Start <= t && t <= x.End);
        Timeline.ToolTip = u == null ? Clock(t) : $"{Clock(t)} · {u.Speaker}: {Shorten(u.Text, 90)}";
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---------- Шапка ----------

    void Title_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Rename_Click(sender, e);
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_call == null) return;
        TitleEdit.Text = _call.Title;
        TitleText.Visibility = Visibility.Collapsed;
        TitleEdit.Visibility = Visibility.Visible;
        TitleEdit.Focus();
        TitleEdit.SelectAll();
    }

    void TitleEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) EndRename(save: true);
        else if (e.Key == Key.Escape) { EndRename(save: false); e.Handled = true; }
    }

    void TitleEdit_LostFocus(object sender, RoutedEventArgs e) => EndRename(save: true);

    void EndRename(bool save)
    {
        if (TitleEdit.Visibility != Visibility.Visible) return;
        TitleEdit.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        var title = TitleEdit.Text.Trim();
        if (!save || _call == null || title.Length == 0 || title == _call.Title) return;
        try
        {
            _call.Rename(title);
            _call.Notify();
            TitleText.Text = title;
        }
        catch (Exception ex) { Log.Write($"Название не сохранено: {ex.Message}"); }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_call == null) return;
        var transcript = Path.Combine(_call.Folder, "transcript.md");
        var text = _summary?.ToClipboard() ?? (File.Exists(transcript) ? File.ReadAllText(transcript) : null);
        if (text == null) return;
        try { Clipboard.SetText(text); } catch { return; }   // буфер иногда занят другим приложением
        SubtitleText.Text = _summary != null ? "Саммари скопировано" : "Транскрипт скопирован";
        var restore = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        var call = _call;
        restore.Tick += (_, _) => { restore.Stop(); if (_call == call) ShowSubtitle(call); };
        restore.Start();
    }

    void ShowSubtitle(CallRecord call) =>
        SubtitleText.Text = $"{call.App} · {call.StartedAt.ToString("d MMMM yyyy, HH:mm", Ru)}" +
                            (call.Duration > TimeSpan.Zero ? $" · {CallRecord.FormatDuration(call.Duration)}" : "");

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_call != null) Shell(_call.Folder);
    }

    void OpenTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (_call != null) Shell(Path.Combine(_call.Folder, "transcript.md"));
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        MoreButton.ContextMenu.PlacementTarget = MoreButton;
        MoreButton.ContextMenu.Placement = PlacementMode.Bottom;
        MoreButton.ContextMenu.IsOpen = true;
    }

    void Reprocess_Click(object sender, RoutedEventArgs e)
    {
        if (_call == null || _call.State is CallState.Recording or CallState.Processing) return;
        _player?.Dispose();   // обработка читает те же wav
        _player = null;
        _host.Reprocess(_call.Folder);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_call == null) return;
        if (_call.State is CallState.Recording or CallState.Processing)
        {
            MessageBox.Show(this, "Звонок сейчас записывается или обрабатывается — удалить его можно будет после.", "Удалить звонок");
            return;
        }
        if (MessageBox.Show(this, $"Удалить звонок «{_call.Title}»?\n\nПапка с записью, транскриптом и саммари попадёт в корзину.",
                "Удалить звонок", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var folder = _call.Folder;
        ShowCall(null);   // закрыть плеер: он держит wav-файлы
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(folder,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            Log.Write($"Звонок удалён в корзину: {folder}");
        }
        catch (Exception ex) { Log.Write($"Не удалось удалить {folder}: {ex.Message}"); }
        Reload(null);
    }

    void Settings_Click(object sender, RoutedEventArgs e) => _host.OpenSettings();

    Brush Res(string key) => TryFindResource(key) as Brush ?? System.Windows.SystemColors.ControlTextBrush;

    static void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }
}
