using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RecordSummary;

public partial class VideoSourceWindow : Window
{
    readonly VideoTarget? _current;
    public VideoTarget? SelectedTarget => Sources.SelectedItem as VideoTarget;

    public VideoSourceWindow(VideoTarget? current)
    {
        InitializeComponent();
        WindowFit.Fit(this);
        _current = current;
        CurrentSource.Text = current != null
            ? $"Сейчас: {current.Title}\nВыбор нового источника не прерывает запись звука."
            : "Выберите окно или весь монитор. Свёрнутые окна не показаны.";
        RefreshSources();
    }

    void RefreshSources()
    {
        var selected = SelectedTarget ?? _current;
        var targets = VideoRecorder.ListTargets();
        Sources.ItemsSource = targets;
        Sources.SelectedItem = targets.FirstOrDefault(t => t.Window == selected?.Window && t.Display == selected?.Display);
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSources();
    void Sources_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChooseButton != null) ChooseButton.IsEnabled = SelectedTarget != null;
    }
    void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTarget != null) DialogResult = true;
    }
    void Sources_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Sources, e.OriginalSource as DependencyObject) is ListBoxItem)
            Choose_Click(sender, e);
    }
}
