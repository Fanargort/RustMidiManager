using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace RustMidiManager;

static class Dialogs
{
    static (Window Window, StackPanel Body) Frame(Window owner, string title, double width)
    {
        var content = new StackPanel { Margin = new Thickness(21) };
        var dialog = new Window
        {
            Owner = owner, Title = title, Width = width, SizeToContent = SizeToContent.Height, MaxHeight = 720,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = (Brush)owner.FindResource("CanvasBrush"), Foreground = (Brush)owner.FindResource("Ink"),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Content = content
        };
        content.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        return (dialog, content);
    }

    static Button Action(Panel panel, string label, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = label, Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 8, 8, 0),
            BorderThickness = new Thickness(0)
        };
        button.Click += click;
        panel.Children.Add(button);
        return button;
    }

    public static string? Input(Window owner, string title, string value = "")
    {
        var (window, body) = Frame(owner, title, 430);
        var field = new TextBox { Text = value, MaxLength = 180, Padding = new Thickness(7) };
        body.Children.Add(field);
        var buttons = new WrapPanel();
        body.Children.Add(buttons);
        Action(buttons, "Сохранить", (_, _) => { if (!string.IsNullOrWhiteSpace(field.Text)) window.DialogResult = true; }).IsDefault = true;
        Action(buttons, "Отмена", (_, _) => window.DialogResult = false).IsCancel = true;
        window.Loaded += (_, _) => { field.Focus(); field.SelectAll(); };
        return window.ShowDialog() == true ? field.Text.Trim() : null;
    }

    public static string? Choose(Window owner, string title, IEnumerable<string> items)
    {
        var values = items.ToArray();
        if (values.Length == 0) return null;
        var (window, body) = Frame(owner, title, 480);
        var list = new ComboBox { ItemsSource = values, SelectedIndex = 0, Padding = new Thickness(7) };
        body.Children.Add(list);
        Action(body, "Выбрать", (_, _) => window.DialogResult = true).IsDefault = true;
        return window.ShowDialog() == true ? list.SelectedItem as string : null;
    }

    public static void Commands(Window owner, string title, string description, string content, string? export)
    {
        var (window, body) = Frame(owner, title, 700);
        body.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var list = new ListBox
        {
            ItemsSource = lines, SelectedIndex = 0, MinHeight = 52, MaxHeight = 225,
            Background = (Brush)owner.FindResource("Surface"), Foreground = (Brush)owner.FindResource("Ink"),
            FontFamily = new FontFamily("Consolas"), Padding = new Thickness(5)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        body.Children.Add(list);
        var hint = new TextBlock { Margin = new Thickness(0, 9, 0, 0) };
        body.Children.Add(hint);
        var buttons = new WrapPanel();
        body.Children.Add(buttons);
        if (export != null)
        {
            Action(buttons, "Копировать строку", (_, _) =>
            {
                if (list.SelectedItem is not string command) return;
                Clipboard.SetText(command);
                hint.Text = $"Скопирована строка {list.SelectedIndex + 1} из {lines.Length}.";
                if (list.SelectedIndex + 1 < lines.Length) list.SelectedIndex++;
                list.ScrollIntoView(list.SelectedItem);
            });
            Action(buttons, "Копировать все", (_, _) => { Clipboard.SetText(content); hint.Text = "Все строки скопированы."; });
            Action(buttons, "Экспорт", (_, _) =>
            {
                var dialog = new SaveFileDialog { FileName = export, Filter = "Файл команд|*" + Path.GetExtension(export), OverwritePrompt = true };
                if (dialog.ShowDialog(window) != true) return;
                Atomic.Write(dialog.FileName, Encoding.UTF8.GetBytes(content + Environment.NewLine));
                hint.Text = "Файл сохранён.";
            });
        }
        Action(buttons, "Закрыть", (_, _) => window.Close()).IsCancel = true;
        window.ShowDialog();
    }
}
