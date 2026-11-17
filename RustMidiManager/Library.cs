using System.Security.Cryptography;
using System.Text.Json;

namespace RustMidiManager;

public sealed class Song
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Original { get; set; } = "";
    public double Duration { get; set; }
    public int Tracks { get; set; }
    public int Format { get; set; }
    public long Size { get; set; }
    public bool Favorite { get; set; }
    public string FileName => "rmm_" + Id + ".mid";
    public string Stem => Path.GetFileNameWithoutExtension(FileName);
    public string Time => MusicLibrary.Time(Duration);
}

public sealed class Playlist
{
    public string Name { get; set; } = "";
    public List<string> Songs { get; set; } = new();
}

public sealed class Catalog
{
    public string Rust { get; set; } = "";
    public string Library { get; set; } = "";
    public bool AutoDetect { get; set; } = true;
    public bool Tray { get; set; } = true;
    public bool StartInTray { get; set; }
    public bool StartWithWindows { get; set; }
    public bool Recursive { get; set; } = true;
    public bool LightMode { get; set; }
    public string Theme { get; set; } = "Violet";
    public int Volume { get; set; } = 75;
    public string? Selected { get; set; }
    public List<Song> Songs { get; set; } = new();
    public List<Playlist> Playlists { get; set; } = new();
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ResetKey { get; set; } = "";
    public string CycleKey { get; set; } = "";
    public string CyclePlaylist { get; set; } = "";
}

sealed class MusicLibrary
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RustMidiManager");
    public static string Time(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    public static bool IsMidi(string path) => Path.GetExtension(path).Equals(".mid", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".midi", StringComparison.OrdinalIgnoreCase);

    public string Root { get; }
    string StatePath => Path.Combine(Root, "library.json");
    public Catalog Data { get; }
    public string Recovery { get; } = "";
    public string Media => string.IsNullOrWhiteSpace(Data.Library) ? Path.Combine(Root, "MIDI") : Data.Library;

    public MusicLibrary(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        try
        {
            Data = File.Exists(StatePath)
                ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(StatePath)) ?? throw new JsonException()
                : new Catalog();
            if (Data.Songs == null || Data.Playlists == null || Data.Bindings == null ||
                Data.Songs.Any(song => song == null || song.Id == null || song.Id.Length != 64 ||
                    song.Id.Any(c => !Uri.IsHexDigit(c)) || !double.IsFinite(song.Duration) ||
                    song.Duration < 0 || song.Duration >= TimeSpan.MaxValue.TotalSeconds ||
                    song.Title == null || song.Original == null) ||
                Data.Playlists.Any(list => list?.Name == null || list.Songs == null))
                throw new JsonException();
        }
        catch (JsonException)
        {
            string backup = Path.Combine(root, $"library-damaged-{Guid.NewGuid():N}.json");
            File.Move(StatePath, backup);
            Data = new Catalog();
            Recovery = $"Повреждённый каталог сохранён в {backup}.";
        }
    }

    public void Save() => Atomic.Write(StatePath, JsonSerializer.SerializeToUtf8Bytes(Data, new JsonSerializerOptions { WriteIndented = true }));
    public string PathFor(Song song) => Path.Combine(Media, song.FileName);

    public static Song ReadSong(string path, out byte[] bytes)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Размер MIDI превышает 16 МБ.");
        bytes = File.ReadAllBytes(path);
        var midi = MidiFile.Read(bytes);
        return new Song
        {
            Id = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Title = Path.GetFileNameWithoutExtension(path),
            Original = path, Duration = midi.Duration, Tracks = midi.Tracks, Format = midi.Format, Size = bytes.Length
        };
    }

    public void PublishSongs(IEnumerable<Song> songs, string rust)
    {
        string folder = RustFiles.Instruments(rust);
        foreach (var song in songs)
        {
            string path = Path.Combine(folder, song.FileName);
            if (File.Exists(path))
            {
                if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) == song.Id) continue;
                throw new IOException($"В папке Rust уже существует {song.FileName} с другим содержимым.");
            }
            byte[] bytes = File.ReadAllBytes(PathFor(song));
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != song.Id)
                throw new IOException($"Файл «{song.Title}» изменён. Импортируйте исходный MIDI повторно.");
            Atomic.Write(path, bytes);
        }
    }
}

static class Atomic
{
    public static void Write(string path, byte[] bytes)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
