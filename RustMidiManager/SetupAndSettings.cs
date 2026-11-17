using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace RustMidiManager;

public partial class MainWindow
{
    async void Setup(object sender, RoutedEventArgs e)
    {
        string rust = Data.Rust;
        if (!RustFiles.IsInstall(rust))
        {
            ShowPage("Settings");
            Report("Укажите папку установки Rust или нажмите «Найти».");
            return;
        }
        if (importing || syncing) { Report("Дождитесь окончания операции с MIDI."); return; }
        string keyFile = RustFiles.KeyFile(rust);
        if (!File.Exists(keyFile))
        {
            MessageBox.Show(this, "Откройте Rust, нажмите F1 и выполните writecfg. Затем снова откройте мастер, чтобы проверить занятые клавиши и сохранить копию.",
                "Шаг 1. Сохраните текущие привязки");
            return;
        }
        string? mode = Dialogs.Choose(this, "Выберите режим клавиш Rust", ["Сразу проигрывать по клавише", "Выбор для ПКМ · экспериментально"]);
        if (mode == null) return;
        bool direct = mode == "Сразу проигрывать по клавише";
        bool includeCycle = !string.IsNullOrEmpty(Data.CycleKey) &&
            Confirm("Включить экспериментальную клавишу цикла? По завершении настройки проверьте переход между композициями в Rust.");
        BindingPlan plan;
        try { plan = BindingPlan.Create(Data, includeCycle, direct); }
        catch (InvalidOperationException error) { ShowPage("Bindings"); Report(error.Message); return; }

        byte[] current = File.ReadAllBytes(keyFile);
        string defaultsPath = Path.Combine(Path.GetDirectoryName(keyFile)!, "keys_default.cfg");
        byte[]? defaults = File.Exists(defaultsPath) ? File.ReadAllBytes(defaultsPath) : null;
        var existing = RustFiles.Existing(keyFile, current);
        if (defaults != null)
            foreach (var entry in RustFiles.Existing(defaultsPath, defaults)) existing.TryAdd(entry.Key, entry.Value);
        var conflicts = plan.Commands.Keys.SelectMany(key => existing
            .Where(pair => RustFiles.Conflicts(pair.Key, key) && !pair.Value.Equals(RustFiles.Bind(key, plan.Commands[key]), StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)).Distinct().ToArray();
        string summary = conflicts.Length == 0 ? "В сохранённом keys.cfg совпадений с новыми назначениями нет."
            : "Найдены действующие назначения для выбранных клавиш. Применение набора изменит их или повлияет на сочетания:\n\n" + string.Join("\n", conflicts);
        if (!Confirm(summary + "\n\nЕсли Rust сейчас открыт, сначала выполните writecfg в F1, чтобы проверка учла свежие изменения. Продолжить с текущим снимком keys.cfg?"))
            return;
        if (!current.AsSpan().SequenceEqual(File.ReadAllBytes(keyFile)))
        { Report("keys.cfg изменился. Откройте мастер ещё раз."); return; }
        string backup = SetupBackup.Make(library.Root, rust, plan.Commands.Keys, current, defaults);
        string errorMessage = await SyncMusic(plan.Songs);
        if (errorMessage.Length != 0) { Report(errorMessage.Trim()); return; }
        string use = direct ? "3. Закройте консоль, возьмите инструмент и нажмите клавишу песни."
            : "3. Закройте консоль, возьмите инструмент, нажмите клавишу песни и удерживайте ПКМ.";
        Dialogs.Commands(this, "Настройка Rust",
            "1. Откройте консоль Rust клавишей F1.\n2. Копируйте и выполняйте строки по одной. Последняя строка сохраняет изменения.\n" + use +
            "\n\nРезервная копия готова. Восстановление доступно на странице «Горячие клавиши».",
            plan.ConsoleText, "rust-midi-bindings.cfg");
        Report($"Команды подготовлены. Копия keys.cfg · {backup}");
    }

    void Restore(object sender, RoutedEventArgs e)
    {
        string root = Path.Combine(library.Root, "Backups");
        if (!Directory.Exists(root)) { Report("Резервных копий пока нет."); return; }
        var snapshots = Directory.GetDirectories(root).Where(path => File.Exists(Path.Combine(path, "restore.txt")))
            .Select(Path.GetFileName).OfType<string>().OrderDescending().ToArray();
        if (snapshots.Length == 0) { Report("Резервных копий пока нет."); return; }
        string? snapshot = Dialogs.Choose(this, "Выберите резервную копию привязок", snapshots);
        if (snapshot == null) return;
        string directory = Path.Combine(root, snapshot);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<SetupBackup>(File.ReadAllText(Path.Combine(directory, "setup.json")))
            ?? throw new InvalidDataException("Не удалось прочитать сведения о резервной копии.");
        if (!Confirm($"Сформировать восстановление клавиш {string.Join(", ", manifest.Keys.Select(key => key.ToUpperInvariant()))}?\nСнимок сделан для {manifest.Rust}. При выполнении команд эти клавиши вернутся к сохранённому состоянию."))
            return;
        Dialogs.Commands(this, "Восстановление клавиш Rust",
            "Откройте F1 в соответствующей установке Rust и выполните строки по одной, включая writecfg. Для проверки откройте настройки управления в игре.",
            RustFiles.Restore(Path.Combine(directory, "keys.cfg"), manifest.Keys), "rust-midi-restore.cfg");
    }

    async Task<string> SyncMusic(IEnumerable<Song>? songs = null)
    {
        string rust = Data.Rust;
        if (!RustFiles.IsInstall(rust) || syncing) return "";
        syncing = true;
        try
        {
            var snapshot = (songs ?? Data.Songs).ToArray();
            await Task.Run(() => library.PublishSongs(snapshot, rust));
            return "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return " · Не удалось скопировать MIDI в Rust: " + error.Message;
        }
        finally
        {
            syncing = false;
            UpdateRustStatus();
        }
    }

    void UpdateRustStatus()
    {
        if (!RustFiles.IsInstall(Data.Rust)) RustStatus.Text = "Rust не найден\nТребуется настройка";
        else RustStatus.Text = Directory.Exists(Path.Combine(Data.Rust, "instruments"))
            ? "Rust найден\nПапка MIDI готова" : "Rust найден\nТребуется папка MIDI";
    }

    void LoadSettings()
    {
        RustPath.Text = Data.Rust;
        LibraryPath.Text = library.Media;
        AutoDetect.IsChecked = Data.AutoDetect;
        Recursive.IsChecked = Data.Recursive;
        StartInTray.IsChecked = Data.StartInTray;
        UseTray.IsChecked = Data.Tray;
        StartWindows.IsChecked = Data.StartWithWindows;
        Theme.SelectedItem = Data.Theme;
        if (Theme.SelectedIndex < 0) Theme.SelectedIndex = 0;
        Volume.Value = Math.Clamp(Data.Volume, 0, 100);
        preview.SetVolume(Data.Volume);
    }

    void ToggleTheme(object sender, RoutedEventArgs e)
    {
        Data.LightMode = !Data.LightMode;
        ApplyTheme();
        library.Save();
    }

    void ApplyTheme()
    {
        bool light = Data.LightMode;
        var app = Application.Current;
#pragma warning disable WPF0001 // WPF Fluent updates the native controls and window chrome together.
        app.ThemeMode = light ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001
        void Brush(string name, string day, string night)
        {
            app.Resources[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? day : night));
        }
        Brush("CanvasBrush", "#F8F7F4", "#191A1C");
        Brush("Rail", "#EFEEEA", "#202123");
        Brush("Surface", "#FFFDFB", "#232426");
        Brush("ControlSurface", "#EAE8E5", "#333437");
        Brush("Line", "#D8D6D2", "#404145");
        Brush("Ink", "#252427", "#F5F3F0");
        Brush("Muted", "#605F64", "#BAB9BE");
        bool blue = Data.Theme == "Blue";
        Brush("Accent", blue ? "#345C83" : "#72546F", blue ? "#A9C6DF" : "#D6B5CE");
        Brush("Selection", blue ? "#DDE7F1" : "#ECE2E9", blue ? "#32404E" : "#433842");
        Brush("AccentInk", "#FFFFFF", "#211D21");
        ThemeToggle.Content = light ? "☾   Тёмная тема" : "☼   Светлая тема";
    }

    async void DetectRust(object sender, RoutedEventArgs e)
    {
        Report("Ищу библиотеки Steam…");
        string? path = await Task.Run(() => RustFiles.Discover());
        if (path == null) { Report("Установку Rust не удалось найти в Steam. Выберите папку вручную."); return; }
        RustPath.Text = path;
        await StoreRustPath();
    }

    async void PickRust(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка установки Rust" };
        if (dialog.ShowDialog(this) != true) return;
        RustPath.Text = dialog.FolderName;
        await StoreRustPath();
    }

    async Task StoreRustPath()
    {
        if (importing || syncing) { Report("Дождитесь завершения текущей операции."); return; }
        string path = RustPath.Text.Trim();
        if (path.Length > 0 && !RustFiles.IsInstall(path))
        { Report("Папка должна содержать RustClient.exe и RustClient_Data."); return; }
        Data.Rust = path.Length > 0 ? Path.GetFullPath(path) : "";
        library.Save();
        string error = await SyncMusic();
        UpdateRustStatus();
        Report(path.Length == 0 ? "Путь установки сброшен." :
            error.Length == 0 ? "Путь сохранён. Папка MIDI готова." : error.Trim());
    }

    async void PickLibrary(object sender, RoutedEventArgs e)
    {
        if (importing || syncing) { Report("Дождитесь завершения текущей операции."); return; }
        var dialog = new OpenFolderDialog { Title = "Папка для копий MIDI" };
        if (dialog.ShowDialog(this) != true) return;
        string target = dialog.FolderName;
        if (Path.GetFullPath(library.Media).TrimEnd(Path.DirectorySeparatorChar).Equals(
            Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
        syncing = true;
        try
        {
            var files = Data.Songs.Select(song => (From: library.PathFor(song), To: Path.Combine(target, song.FileName))).ToArray();
            Report("Копирую медиатеку…");
            await Task.Run(() =>
            {
                foreach (var file in files)
                {
                    byte[] bytes = File.ReadAllBytes(file.From);
                    if (!File.Exists(file.To)) Atomic.Write(file.To, bytes);
                    else if (!bytes.AsSpan().SequenceEqual(File.ReadAllBytes(file.To)))
                        throw new IOException($"Файл {file.To} уже существует с другим содержимым.");
                }
            });
            Data.Library = target;
            library.Save();
            LibraryPath.Text = library.Media;
            Report("Новое расположение медиатеки сохранено.");
        }
        finally { syncing = false; }
    }

    async void SaveSettings(object sender, RoutedEventArgs e)
    {
        if (importing || syncing) { Report("Дождитесь завершения текущей операции."); return; }
        string path = RustPath.Text.Trim();
        if (path.Length > 0 && !RustFiles.IsInstall(path))
        { Report("Проверьте папку установки Rust."); return; }
        bool startup = StartWindows.IsChecked == true;
        if (startup != Data.StartWithWindows)
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (startup) key.SetValue("RustMidiManager", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("RustMidiManager", false);
        }
        Data.StartWithWindows = startup;
        Data.AutoDetect = AutoDetect.IsChecked == true;
        Data.Recursive = Recursive.IsChecked == true;
        Data.Tray = UseTray.IsChecked == true;
        Data.StartInTray = StartInTray.IsChecked == true;
        Data.Theme = Theme.SelectedItem as string ?? "Violet";
        ApplyTheme();
        if (!Data.Tray) tray.Visible = false;
        if (path != Data.Rust) await StoreRustPath();
        else { library.Save(); Report("Настройки сохранены."); }
    }

    void OpenMidiFolder(object sender, RoutedEventArgs e)
    {
        string folder = RustFiles.Instruments(Data.Rust);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        UpdateRustStatus();
    }

    void WindowStateChanged(object? sender, EventArgs e)
    {
        if (!ready || WindowState != WindowState.Minimized || !Data.Tray) return;
        tray.Visible = true;
        Hide();
    }

    void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        tray.Visible = false;
    }

    void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!exiting && (importing || syncing))
        {
            e.Cancel = true;
            Report("Дождитесь завершения текущей операции.");
            return;
        }
        volumeSave.Stop();
        library.Save();
        preview.Dispose();
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
    }
}
