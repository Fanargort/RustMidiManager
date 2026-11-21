using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RustMidiManager;

public partial class MainWindow
{
    async void ConvertAudio(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "MP3 → MIDI · мелодия", Filter = "Аудио (*.mp3;*.wav)|*.mp3;*.wav" };
        if (dialog.ShowDialog(this) != true) return;
        var converter = new ConverterWindow(this, dialog.FileName);
        if (converter.ShowDialog() == true && converter.OutputPath != null)
            await Import([converter.OutputPath]);
    }
}

internal sealed class ConverterWindow : Window
{
    readonly CancellationTokenSource cancellation = new();
    readonly Button convert = new() { Content = "Создать MIDI", HorizontalAlignment = HorizontalAlignment.Left };
    readonly ComboBox range = new() { ItemsSource = new[] { "Обычная мелодия · C3–C6", "Высокая мелодия · C4–C7", "Низкая мелодия · C2–C5" }, SelectedIndex = 0 };
    readonly ProgressBar progress = new() { Height = 5, Maximum = 100, Margin = new Thickness(0, 16, 0, 8) };
    readonly TextBlock status = new() { Text = "Выберите диапазон мелодии и нажмите «Создать MIDI».", Margin = new Thickness(0, 0, 0, 16) };
    readonly string source;
    bool busy;
    public string? OutputPath { get; private set; }

    public ConverterWindow(Window owner, string source)
    {
        Owner = owner;
        this.source = source;
        Title = "MP3 → MIDI";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        SetResourceReference(ForegroundProperty, "Ink");
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = "Мелодия из аудио", FontSize = 24, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = Path.GetFileName(source), Margin = new Thickness(0, 8, 0, 14) });
        body.Children.Add(new TextBlock { Text = "Экспериментальное распознавание одной линии нот. Лучше работает с сольным инструментом или отчётливым голосом. В полной песне бас, ударные и аккорды могут влиять на результат. Длительность — до 10 минут.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        body.Children.Add(range);
        body.Children.Add(progress);
        body.Children.Add(status);
        var buttons = new WrapPanel();
        buttons.Children.Add(convert);
        var close = new Button { Content = "Закрыть" };
        close.Click += (_, _) => { if (busy) { cancellation.Cancel(); status.Text = "Останавливаю…"; } else Close(); };
        buttons.Children.Add(close);
        body.Children.Add(buttons);
        Content = body;
        convert.Click += Start;
        Closing += OnClosing;
        Closed += (_, _) => cancellation.Dispose();
    }

    async void Start(object sender, RoutedEventArgs e)
    {
        var save = new SaveFileDialog { Title = "Сохранить мелодию", Filter = "MIDI (*.mid)|*.mid", FileName = Path.GetFileNameWithoutExtension(source) + " - melody.mid", OverwritePrompt = true };
        if (save.ShowDialog(this) != true) return;
        busy = true;
        convert.IsEnabled = range.IsEnabled = false;
        status.Text = "Распознаю мелодию…";
        int minimum = range.SelectedIndex switch { 1 => 60, 2 => 36, _ => 48 };
        try
        {
            var updates = new Progress<int>(value => progress.Value = value);
            byte[] bytes = await Task.Run(() => MelodyConverter.Convert(source, minimum, cancellation.Token, updates));
            cancellation.Token.ThrowIfCancellationRequested();
            _ = MidiFile.Read(bytes);
            Atomic.Write(save.FileName, bytes);
            OutputPath = save.FileName;
            busy = false;
            DialogResult = true;
        }
        catch (OperationCanceledException) { busy = false; Close(); }
        catch (Exception error)
        {
            status.Text = "Не удалось обработать аудио. " + error.Message;
            convert.IsEnabled = range.IsEnabled = true;
        }
        finally { busy = false; }
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!busy) return;
        cancellation.Cancel();
        status.Text = "Останавливаю…";
        e.Cancel = true;
    }
}
