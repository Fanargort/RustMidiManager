using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace RustMidiManager;

public partial class MainWindow : Window
{
    readonly MusicLibrary library;
    readonly Preview preview = new();
    readonly DispatcherTimer volumeSave = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Forms.NotifyIcon tray;
    readonly Forms.ToolStripMenuItem traySong;
    Catalog Data => library.Data;
    string page = "Overview";
    bool refreshing, importing, ready, exiting, syncing, loadingPreview;
    Song? loadedPreview;
    sealed record SongRow(Song Song, string Keys)
    {
        public string Title => Song.Title;
        public string Time => Song.Time;
        public int Tracks => Song.Tracks;
        public string Star => Song.Favorite ? "★" : "";
    }
    sealed record KeyRow(string Key, Song? Song, string Label);

    public MainWindow()
    {
        library = new MusicLibrary(App.DataDirectory);
        InitializeComponent();
        Sort.ItemsSource = new[] { "В порядке списка", "Название А–Я", "Название Я–А", "По длительности" };
        Sort.SelectedIndex = 0;
        BindKey.ItemsSource = RustFiles.Keys;
        ResetKey.ItemsSource = CycleKey.ItemsSource = new[] { "—" }.Concat(RustFiles.Keys).ToArray();
        Theme.ItemsSource = new[] { "Violet", "Blue" };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть Rust MIDI Manager", null, (_, _) => ShowWindow());
        traySong = new Forms.ToolStripMenuItem("Композиция не выбрана") { Enabled = false };
        menu.Items.Add(traySong);
        menu.Items.Add("Предыдущая композиция", null, (_, _) => Step(-1));
        menu.Items.Add("Следующая композиция", null, (_, _) => Step(1));
        menu.Items.Add("Открыть папку instruments", null, (_, _) => OpenMidiFolder(this, new RoutedEventArgs()));
        menu.Items.Add("Выход", null, (_, _) => { exiting = true; Close(); });
        tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(Application.GetResourceStream(new Uri("Brand.ico", UriKind.Relative)).Stream, 32, 32), Text = "Rust MIDI Manager", ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowWindow();
        preview.Changed += UpdatePreview;
        volumeSave.Tick += (_, _) => { volumeSave.Stop(); library.Save(); };
        LoadSettings();
        ApplyTheme();
        ready = true;
        RefreshLists();
        ShowPage("Library");
        Report(string.IsNullOrEmpty(library.Recovery) ? "Перетащите MIDI-файлы или папки в окно." : library.Recovery);
        Loaded += OnLoaded;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (Data.StartInTray && Data.Tray) WindowState = WindowState.Minimized;
        if (Data.AutoDetect && !RustFiles.IsInstall(Data.Rust))
        {
            string? path = await Task.Run(() => RustFiles.Discover());
            if (path != null && !RustFiles.IsInstall(Data.Rust))
            {
                Data.Rust = path;
                library.Save();
                RustPath.Text = path;
            }
        }
        if (RustFiles.IsInstall(Data.Rust))
        {
            string error = await SyncMusic();
            if (error.Length != 0) Report("Rust найден" + error);
        }
        UpdateRustStatus();
    }

    void Report(string message) { Status.Text = message; Status.ToolTip = message; }
    Song? Current => Data.Songs.FirstOrDefault(song => song.Id == Data.Selected);
    Playlist? ActiveList => PlaylistView.SelectedItem as Playlist;

    void Navigate(object sender, RoutedEventArgs e) => ShowPage((string)((Button)sender).Tag);
    void ShowPage(string name)
    {
        page = name;
        Overview.Visibility = name == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        Settings.Visibility = name == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        Bindings.Visibility = name == "Bindings" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = name is "Library" or "Lists" or "Favorites" ? Visibility.Visible : Visibility.Collapsed;
        PlaylistBar.Visibility = RemoveFromListButton.Visibility = name == "Lists" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { OverviewNav, LibraryNav, ListsNav, FavoritesNav, BindingsNav, SettingsNav })
            if ((string)button.Tag == name) button.SetResourceReference(Button.BackgroundProperty, "Selection");
            else button.Background = Brushes.Transparent;
        Heading.Text = name switch
        {
            "Overview" => "Обзор", "Library" => "Медиатека", "Lists" => "Плейлисты", "Favorites" => "Избранное",
            "Bindings" => "Горячие клавиши", _ => "Настройки"
        };
        Subtitle.Text = name switch
        {
            "Overview" => "Подготовьте музыку для инструментов Rust",
            "Lists" => "Меняйте порядок композиций в списках",
            "Bindings" => "Назначения применяются через консоль F1",
            "Settings" => "Установка Rust и настройки приложения",
            _ => $"{Data.Songs.Count} в медиатеке"
        };
        RefreshRows();
    }

    void RefreshLists()
    {
        refreshing = true;
        var chosen = ActiveList;
        PlaylistView.ItemsSource = Data.Playlists.ToArray();
        PlaylistView.SelectedItem = chosen;
        if (PlaylistView.SelectedIndex == -1 && Data.Playlists.Count > 0) PlaylistView.SelectedIndex = 0;
        CyclePlaylist.ItemsSource = Data.Playlists.ToArray();
        CyclePlaylist.SelectedItem = Data.Playlists.FirstOrDefault(list => list.Name == Data.CyclePlaylist);
        ResetKey.SelectedItem = string.IsNullOrEmpty(Data.ResetKey) ? "—" : Data.ResetKey;
        CycleKey.SelectedItem = string.IsNullOrEmpty(Data.CycleKey) ? "—" : Data.CycleKey;
        BindSong.ItemsSource = Data.Songs.OrderBy(song => song.Title).ToArray();
        BindSong.SelectedItem = Current;
        if (BindKey.SelectedIndex < 0) BindKey.SelectedItem = "f6";
        var keyRows = Data.Bindings.OrderBy(pair => Array.IndexOf(RustFiles.Keys, pair.Key))
            .Select(pair => new KeyRow(pair.Key, Data.Songs.FirstOrDefault(song => song.Id == pair.Value), ""));
        BindingList.ItemsSource = keyRows.Where(row => row.Song != null)
            .Select(row => row with { Label = $"{row.Key.ToUpperInvariant()}    {row.Song!.Title}" }).ToArray();
        OverviewCounts.Text = $"Композиции  {Data.Songs.Count}     Плейлисты  {Data.Playlists.Count}     Назначения  {Data.Bindings.Count}";
        refreshing = false;
        if (page is "Library" or "Favorites") Subtitle.Text = $"{Data.Songs.Count} в медиатеке";
        RefreshRows();
        UpdateRustStatus();
    }

    void RefreshRows()
    {
        if (!ready || refreshing) return;
        refreshing = true;
        IEnumerable<Song> songs = Data.Songs;
        if (page == "Favorites") songs = songs.Where(song => song.Favorite);
        if (page == "Lists")
        {
            var byId = Data.Songs.ToDictionary(song => song.Id);
            songs = ActiveList?.Songs.Where(byId.ContainsKey).Select(id => byId[id]) ?? [];
        }
        string query = Search.Text.Trim();
        if (query.Length > 0)
            songs = songs.Where(song => song.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || song.Original.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        songs = Sort.SelectedIndex switch
        {
            1 => songs.OrderBy(song => song.Title, StringComparer.CurrentCultureIgnoreCase),
            2 => songs.OrderByDescending(song => song.Title, StringComparer.CurrentCultureIgnoreCase),
            3 => songs.OrderBy(song => song.Duration), _ => songs
        };
        var rows = songs.Select(song => new SongRow(song, string.Join(", ", Data.Bindings
            .Where(pair => pair.Value == song.Id).Select(pair => pair.Key.ToUpperInvariant())))).ToArray();
        Songs.ItemsSource = rows;
        Songs.SelectedItem = rows.FirstOrDefault(row => row.Song.Id == Data.Selected);
        EmptyTitle.Text = query.Length > 0 ? "Совпадений нет" : page == "Favorites" ? "Пока нет избранных композиций" : page == "Lists" ? "Плейлист пуст" : "Добавьте первую композицию";
        Empty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        refreshing = false;
        ShowSelected();
    }

    void SongChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || refreshing || Songs.SelectedItem is not SongRow row) return;
        Select(row.Song);
    }

    void Select(Song? song)
    {
        if (song?.Id == Data.Selected) return;
        preview.Stop();
        loadedPreview = null;
        Data.Selected = song?.Id;
        BindSong.SelectedItem = song;
        ShowSelected();
        library.Save();
    }

    void ShowSelected()
    {
        SelectedTitle.Text = Current?.Title ?? "Выберите композицию";
        traySong.Text = Current?.Title ?? "Композиция не выбрана";
        PlayButton.IsEnabled = Current != null && !loadingPreview;
        UpdatePreview();
    }
    void SearchChanged(object sender, TextChangedEventArgs e) => RefreshRows();
    void SortChanged(object sender, SelectionChangedEventArgs e) => RefreshRows();
    void PlaylistChanged(object sender, SelectionChangedEventArgs e) => RefreshRows();

    void Previous(object sender, RoutedEventArgs e) => Step(-1);
    void Next(object sender, RoutedEventArgs e) => Step(1);

    void Step(int direction)
    {
        var songs = (page is "Library" or "Lists" or "Favorites" ? (Songs.ItemsSource as SongRow[])?.Select(row => row.Song) : Data.Songs)?.ToArray() ?? [];
        if (songs.Length == 0) return;
        int index = Array.FindIndex(songs, song => song.Id == Data.Selected);
        if (index < 0) index = direction == 1 ? -1 : 0;
        Select(songs[(index + direction + songs.Length) % songs.Length]);
        RefreshRows();
        if (Songs.SelectedItem != null) Songs.ScrollIntoView(Songs.SelectedItem);
    }

    async void PlayPreview(object sender, RoutedEventArgs e)
    {
        var song = Current;
        if (song == null || loadingPreview) return;
        if (loadedPreview != song)
        {
            loadingPreview = true;
            PlayButton.IsEnabled = false;
            try
            {
                var file = await Task.Run(() => MidiFile.Read(File.ReadAllBytes(library.PathFor(song))));
                if (Current != song) return;
                preview.Load(file);
                loadedPreview = song;
            }
            finally { loadingPreview = false; PlayButton.IsEnabled = Current != null; }
        }
        preview.PlayOrPause();
    }

    void StopPreview(object sender, RoutedEventArgs e) => preview.Stop();
    void UpdatePreview()
    {
        var duration = Current?.Duration ?? 0;
        double position = loadedPreview == Current ? preview.Position : 0;
        PreviewTime.Text = $"{MusicLibrary.Time(position)} / {MusicLibrary.Time(duration)}";
        PreviewProgress.Maximum = Math.Max(1, duration);
        PreviewProgress.Value = position;
        PlayButton.Content = preview.Playing ? "Пауза" : "Играть";
    }

    void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        Data.Volume = (int)e.NewValue;
        preview.SetVolume(Data.Volume);
        volumeSave.Stop();
        volumeSave.Start();
    }

    async void ImportFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "MIDI (*.mid;*.midi)|*.mid;*.midi", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await Import(dialog.FileNames);
    }

    async void ImportFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку с MIDI" };
        if (dialog.ShowDialog(this) == true) await Import([dialog.FolderName]);
    }

    async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await Import(paths);
    }

    async Task Import(string[] paths)
    {
        if (importing || syncing) { Report("Дождитесь окончания текущей операции с MIDI."); return; }
        importing = true;
        try
        {
            Report("Проверяю MIDI и создаю копии…");
            var seen = Data.Songs.Select(song => song.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string folder = library.Media;
            bool recursive = Data.Recursive;
            var result = await Task.Run(() => ImportFilesToLibrary(paths, seen, folder, recursive));
            Data.Songs.AddRange(result.Added);
            if (Current == null && result.Added.Count != 0) Select(result.Added[0]);
            library.Save();
            ShowPage("Library");
            RefreshLists();
            string syncError = await SyncMusic(result.Added);
            Report($"Импортировано {result.Added.Count} · Дубликаты {result.Duplicates} · Ошибки {result.Errors.Count}" + syncError);
            if (result.Errors.Count > 0)
                Dialogs.Commands(this, "Пропущенные файлы", "Следующие элементы не удалось импортировать.", string.Join(Environment.NewLine, result.Errors), null);
        }
        finally { importing = false; }
    }

    internal sealed record ImportResult(List<Song> Added, int Duplicates, List<string> Errors);
    internal static ImportResult ImportFilesToLibrary(IEnumerable<string> paths, HashSet<string> seen, string destination, bool recursive)
    {
        var added = new List<Song>();
        int duplicates = 0;
        var errors = new List<string>();
        void ImportOne(string file)
        {
            try
            {
                if (!MusicLibrary.IsMidi(file)) throw new InvalidDataException("Ожидался файл .mid или .midi.");
                var song = MusicLibrary.ReadSong(file, out byte[] bytes);
                if (seen.Contains(song.Id))
                {
                    string cached = Path.Combine(destination, song.FileName);
                    if (!File.Exists(cached) || !System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(cached))
                        .SequenceEqual(System.Security.Cryptography.SHA256.HashData(bytes)))
                        Atomic.Write(cached, bytes);
                    duplicates++;
                    return;
                }
                Atomic.Write(Path.Combine(destination, song.FileName), bytes);
                seen.Add(song.Id);
                added.Add(song);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { errors.Add($"{file} — {error.Message}"); }
        }
        foreach (var path in paths)
        {
            if (!Directory.Exists(path)) { ImportOne(path); continue; }
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (string file in Directory.EnumerateFiles(path, "*", options).Where(MusicLibrary.IsMidi)) ImportOne(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add($"{path} — {error.Message}"); }
        }
        return new ImportResult(added, duplicates, errors);
    }

    Song? VisibleSong => (Songs.SelectedItem as SongRow)?.Song;
    void Favorite(object sender, RoutedEventArgs e)
    {
        if (VisibleSong is not Song song) return;
        song.Favorite = !song.Favorite;
        library.Save();
        RefreshRows();
    }
    void Rename(object sender, RoutedEventArgs e)
    {
        if (VisibleSong is not Song song) return;
        string? name = Dialogs.Input(this, "Название композиции", song.Title);
        if (name == null) return;
        song.Title = name;
        library.Save();
        RefreshLists();
    }
    void Metadata(object sender, RoutedEventArgs e)
    {
        if (VisibleSong is not Song song) return;
        MessageBox.Show(this, $"{song.Title}\n\nДлительность  {song.Time}\nДорожек  {song.Tracks}\nФормат SMF  {song.Format}\nРазмер  {song.Size:N0} байт\n\nИсходный файл\n{song.Original}\n\nКопия приложения\n{library.PathFor(song)}",
            "Сведения о MIDI", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    void Remove(object sender, RoutedEventArgs e)
    {
        if (VisibleSong is not Song song ||
            !Confirm($"Удалить «{song.Title}» из каталога и плейлистов? Исходный файл и копии сохранятся. Назначения, уже применённые в Rust, можно снять через восстановление привязок.")) return;
        if (Current == song) Select(null);
        Data.Songs.Remove(song);
        foreach (var list in Data.Playlists) list.Songs.RemoveAll(id => id == song.Id);
        foreach (string key in Data.Bindings.Where(pair => pair.Value == song.Id).Select(pair => pair.Key).ToArray()) Data.Bindings.Remove(key);
        library.Save();
        RefreshLists();
    }
    void NewPlaylist(object sender, RoutedEventArgs e)
    {
        string? name = Dialogs.Input(this, "Название плейлиста");
        if (name == null) return;
        if (Data.Playlists.Any(list => list.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        { Report("Плейлист с таким названием уже есть."); return; }
        var playlist = new Playlist { Name = name };
        Data.Playlists.Add(playlist);
        library.Save();
        RefreshLists();
        PlaylistView.SelectedItem = playlist;
    }
    void DeletePlaylist(object sender, RoutedEventArgs e)
    {
        if (ActiveList is not Playlist list || !Confirm($"Удалить плейлист «{list.Name}»? Композиции останутся в медиатеке.")) return;
        if (Data.CyclePlaylist == list.Name) { Data.CyclePlaylist = ""; Data.CycleKey = ""; }
        Data.Playlists.Remove(list);
        library.Save();
        RefreshLists();
    }
    void AddToPlaylist(object sender, RoutedEventArgs e)
    {
        if (VisibleSong is not Song song) return;
        if (Data.Playlists.Count == 0) { ShowPage("Lists"); NewPlaylist(sender, e); }
        if (Data.Playlists.Count == 0) return;
        string? name = Dialogs.Choose(this, "Выберите плейлист", Data.Playlists.Select(list => list.Name));
        if (name == null) return;
        var target = Data.Playlists.First(list => list.Name == name);
        if (!target.Songs.Contains(song.Id)) target.Songs.Add(song.Id);
        library.Save();
        RefreshRows();
        Report($"«{song.Title}» в плейлисте «{name}».");
    }
    void RemoveFromPlaylist(object sender, RoutedEventArgs e)
    {
        if (ActiveList == null || VisibleSong is not Song song) return;
        ActiveList.Songs.Remove(song.Id);
        library.Save();
        RefreshRows();
    }
    void MoveUp(object sender, RoutedEventArgs e) => MoveSong(-1);
    void MoveDown(object sender, RoutedEventArgs e) => MoveSong(1);
    void MoveSong(int direction)
    {
        if (ActiveList is not Playlist list || VisibleSong is not Song song) return;
        int index = list.Songs.IndexOf(song.Id), newIndex = index + direction;
        if (index < 0 || newIndex < 0 || newIndex >= list.Songs.Count) return;
        (list.Songs[index], list.Songs[newIndex]) = (list.Songs[newIndex], list.Songs[index]);
        Sort.SelectedIndex = 0;
        library.Save();
        RefreshRows();
    }

    bool Confirm(string text) => MessageBox.Show(this, text, "Rust MIDI Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    void BindingSelected(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || BindingList.SelectedItem is not KeyRow row) return;
        BindKey.SelectedItem = row.Key;
        BindSong.SelectedItem = row.Song;
    }
    void Assign(object sender, RoutedEventArgs e)
    {
        if (BindSong.SelectedItem is not Song song || BindKey.SelectedItem is not string key) { Report("Выберите композицию и клавишу."); return; }
        if (key == Data.ResetKey || key == Data.CycleKey) { Report("Клавиша занята сбросом или циклом."); return; }
        if (Data.Bindings.TryGetValue(key, out var old) && old != song.Id && !Confirm($"Заменить назначение {key.ToUpperInvariant()} в приложении?")) return;
        Data.Bindings[key] = song.Id;
        library.Save();
        RefreshLists();
        BindSong.SelectedItem = song;
        Report($"{key.ToUpperInvariant()} · {song.Title} назначена в приложении. Примените команды в мастере настройки.");
    }
    void RemoveBind(object sender, RoutedEventArgs e)
    {
        if (BindKey.SelectedItem is not string key || !Data.Bindings.Remove(key)) return;
        library.Save();
        RefreshLists();
        Report("Назначение удалено из проекта. Применённую в игре привязку можно снять через восстановление.");
    }
    void SaveSpecial(object sender, RoutedEventArgs e)
    {
        string reset = ResetKey.SelectedItem as string ?? "—", cycle = CycleKey.SelectedItem as string ?? "—";
        reset = reset == "—" ? "" : reset;
        cycle = cycle == "—" ? "" : cycle;
        string list = (CyclePlaylist.SelectedItem as Playlist)?.Name ?? "";
        if (Data.Bindings.ContainsKey(reset) || Data.Bindings.ContainsKey(cycle) || (reset != "" && reset == cycle))
        { Report("Сброс и цикл требуют отдельных свободных клавиш."); return; }
        if (cycle != "" && (CyclePlaylist.SelectedItem as Playlist)?.Songs.Count is not >= 2)
        { Report("Выберите плейлист с двумя или более композициями."); return; }
        Data.ResetKey = reset;
        Data.CycleKey = cycle;
        Data.CyclePlaylist = list;
        library.Save();
        Report("Дополнительные назначения сохранены в приложении.");
    }

    async void SetRmb(object sender, RoutedEventArgs e)
    {
        if (Current is not Song song) { Report("Выберите композицию в медиатеке."); return; }
        if (!RustFiles.IsInstall(Data.Rust)) { ShowPage("Settings"); Report("Сначала укажите папку установки Rust."); return; }
        if (syncing || importing) { Report("Дождитесь окончания операции с MIDI."); return; }
        string error = await SyncMusic([song]);
        if (error.Length != 0) { Report(error.Trim()); return; }
        Dialogs.Commands(this, "Композиция для ПКМ", "Экспериментальная команда. В Rust откройте консоль F1, выполните команду и проверьте результат с инструментом в руках и удержанием ПКМ. Для документированного запуска по клавише воспользуйтесь мастером настройки.",
            RustFiles.Autoplay(song), "выбор-midi.txt");
        Report("Команда выбора подготовлена. Активный MIDI определяется в игре.");
    }
}

