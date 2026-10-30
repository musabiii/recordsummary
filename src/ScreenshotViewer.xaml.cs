using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace RecordSummary;

/// <summary>Просмотр скриншотов звонка: листание стрелками, "Слушать с этого места", копирование, удаление в корзину.</summary>
public partial class ScreenshotViewer : Window
{
    readonly List<Screenshot> _shots;
    readonly Action<TimeSpan> _listen;
    int _index;

    /// <summary>Скриншот удалили — окно записей перестраивает вкладку и метки.</summary>
    public event Action? Deleted;

    public ScreenshotViewer(List<Screenshot> shots, int index, Action<TimeSpan> listen)
    {
        _shots = shots;
        _listen = listen;
        _index = index;
        InitializeComponent();
        WindowFit.Fit(this);
        PreviewKeyDown += OnKey;
        ShowShot(_index);
    }

    /// <summary>Картинка читается в память целиком, чтобы файл не был занят и его можно было удалить.</summary>
    static BitmapImage Load(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(path);
        img.EndInit();
        img.Freeze();
        return img;
    }

    void ShowShot(int index)
    {
        if (_shots.Count == 0) { Close(); return; }
        _index = Math.Clamp(index, 0, _shots.Count - 1);
        var shot = _shots[_index];
        try { Picture.Source = Load(shot.Path); }
        catch (Exception e) { Picture.Source = null; Log.Write($"Скриншот не открылся: {e.Message}"); }
        CounterText.Text = $"{_index + 1} из {_shots.Count}";
        TimeText.Text = $"{Clock(shot.At)} записи";
        Title = $"Скриншот {Clock(shot.At)}";
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _shots.Count - 1;
    }

    static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: ShowShot(_index - 1); break;
            case Key.Right: ShowShot(_index + 1); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    void Prev_Click(object sender, RoutedEventArgs e) => ShowShot(_index - 1);
    void Next_Click(object sender, RoutedEventArgs e) => ShowShot(_index + 1);

    void Listen_Click(object sender, RoutedEventArgs e) => _listen(_shots[_index].At);

    void Picture_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Shell(_shots[_index].Path);
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Picture.Source is BitmapSource bmp)
            try { Clipboard.SetImage(bmp); } catch { }   // буфер иногда занят другим приложением
    }

    void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", $"/select,\"{_shots[_index].Path}\""); } catch { }
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var shot = _shots[_index];
        if (MessageBox.Show(this, $"Удалить скриншот {Clock(shot.At)}? Он попадёт в корзину.", "Удалить скриншот",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(shot.Path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) { Log.Write($"Скриншот не удалён: {ex.Message}"); return; }
        _shots.RemoveAt(_index);
        Deleted?.Invoke();
        ShowShot(_index);
    }

    static void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }
}
