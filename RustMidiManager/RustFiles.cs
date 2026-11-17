using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RustMidiManager;

static class RustFiles
{
    public static bool IsInstall(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "RustClient.exe"))
        && Directory.Exists(Path.Combine(path, "RustClient_Data"));

    public static IEnumerable<string> SteamLibraries(string steam)
    {
        if (string.IsNullOrWhiteSpace(steam)) yield break;
        yield return steam;
        string file = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file)) yield break;
        foreach (Match match in Regex.Matches(File.ReadAllText(file), """(?m)^\s*"path"\s+"([^"\r\n]+)"|^\s*"\d+"\s+"([A-Za-z]:[^"\r\n]+)" """.TrimEnd()))
        {
            var group = match.Groups[1].Success ? match.Groups[1] : match.Groups[2];
            yield return group.Value.Replace(@"\\", @"\");
        }
    }

    public static string? Discover(IEnumerable<string>? steamRoots = null)
    {
        steamRoots ??= new[]
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", "") as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", "") as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
        }.OfType<string>();
        foreach (string root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (string library in SteamLibraries(root))
                {
                    string apps = Path.Combine(library, "steamapps");
                    string manifest = Path.Combine(apps, "appmanifest_252490.acf");
                    string candidate = Path.Combine(apps, "common", "Rust");
                    if (IsInstall(candidate)) return candidate;
                    if (!File.Exists(manifest)) continue;
                    var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                    if (!match.Success) continue;
                    string name = match.Groups[1].Value;
                    if (Path.GetFileName(name) != name || name is "." or "..") continue;
                    candidate = Path.Combine(apps, "common", name);
                    if (IsInstall(candidate)) return candidate;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    public static string Instruments(string rust)
    {
        if (!IsInstall(rust)) throw new IOException("Выберите папку установки Rust с RustClient.exe и RustClient_Data.");
        return Directory.CreateDirectory(Path.Combine(rust, "instruments")).FullName;
    }

    public static string KeyFile(string rust)
    {
        string cfg = Path.Combine(rust, "cfg", "keys.cfg");
        return File.Exists(cfg) ? cfg : Path.Combine(rust, "keys.cfg");
    }

    public static Dictionary<string, string> Existing(string path, byte[]? snapshot = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var input = snapshot == null ? new StreamReader(path) : new StreamReader(new MemoryStream(snapshot, false));
        while (input.ReadLine() is string line)
        {
            Match match = Regex.Match(line, """^\s*bind(?:_default)?(\s+(?:"([^"]+)"|(\S+))\s+.+)$""", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            string key = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            result[key.Trim('[', ']')] = "bind" + match.Groups[1].Value.TrimEnd();
        }
        return result;
    }

    public static string[] Keys { get; } = Enumerable.Range(1, 12).Where(n => n != 1).Select(n => "f" + n)
        .Concat("abcdefghijklmnopqrstuvwxyz0123456789".Select(value => value.ToString())).ToArray();
    public static bool ValidKey(string key) => Keys.Contains(key, StringComparer.OrdinalIgnoreCase);
    public static bool Conflicts(string existing, string key) =>
        existing.Split('+').Contains(key, StringComparer.OrdinalIgnoreCase);
    public static string Autoplay(Song song)
    {
        if (!Regex.IsMatch(song.Id, "^[0-9a-fA-F]{64}$")) throw new ArgumentException("Недопустимое имя MIDI.");
        return "instruments.overrideautoplay " + song.Stem;
    }
    public static string DirectPlay(Song song) => "instruments.playrecording " + song.Stem;

    public static string Bind(string key, string action)
    {
        if (!ValidKey(key) || !Regex.IsMatch(action, @"^[a-z0-9_.~ ;]+$", RegexOptions.IgnoreCase))
            throw new ArgumentException("Недопустимая клавиша или команда.");
        return $"bind {key.ToLowerInvariant()} {action}";
    }

    public static string Restore(string backup, IEnumerable<string> keys)
    {
        var existing = Existing(backup);
        string defaultsPath = Path.Combine(Path.GetDirectoryName(backup)!, "keys_default.cfg");
        if (File.Exists(defaultsPath))
            foreach (var entry in Existing(defaultsPath)) existing.TryAdd(entry.Key, entry.Value);
        return string.Join(Environment.NewLine, keys.Distinct(StringComparer.OrdinalIgnoreCase).Select(key =>
        {
            if (!ValidKey(key)) throw new ArgumentException("Недопустимая клавиша.");
            return existing.TryGetValue(key, out var line) ? line : $"bind {key} \"\"";
        }).Append("writecfg"));
    }
}

sealed record BindingPlan(Dictionary<string, string> Commands, Song[] Songs)
{
    public string ConsoleText => string.Join(Environment.NewLine, Commands.Select(pair => RustFiles.Bind(pair.Key, pair.Value)).Append("writecfg"));

    public static BindingPlan Create(Catalog catalog, bool cycle, bool direct = false)
    {
        var commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var songs = new HashSet<Song>();
        var byId = catalog.Songs.ToDictionary(song => song.Id);
        void Add(string key, string action)
        {
            _ = RustFiles.Bind(key, action);
            if (!commands.TryAdd(key, action)) throw new InvalidOperationException($"Клавиша {key.ToUpperInvariant()} назначена несколько раз.");
        }
        foreach (var entry in catalog.Bindings)
        {
            if (!byId.TryGetValue(entry.Value, out var song)) continue;
            Add(entry.Key, direct ? RustFiles.DirectPlay(song) : RustFiles.Autoplay(song));
            songs.Add(song);
        }
        if (!direct && !string.IsNullOrEmpty(catalog.ResetKey)) Add(catalog.ResetKey, "instruments.clearautoplayoverride");
        if (cycle && !string.IsNullOrEmpty(catalog.CycleKey))
        {
            var playlist = catalog.Playlists.FirstOrDefault(list => list.Name == catalog.CyclePlaylist);
            var items = playlist?.Songs.Where(byId.ContainsKey).Select(id => byId[id]).Distinct().ToArray() ?? [];
            if (items.Length < 2) throw new InvalidOperationException("Циклу нужны две или более композиции.");
            Add(catalog.CycleKey, "~" + string.Join(';', items.Select(song => direct ? RustFiles.DirectPlay(song) : RustFiles.Autoplay(song))));
            songs.UnionWith(items);
        }
        if (commands.Count == 0) throw new InvalidOperationException("Добавьте хотя бы одно назначение.");
        return new BindingPlan(commands, songs.ToArray());
    }
}

sealed record SetupBackup(string Rust, string[] Keys)
{
    public static string Make(string root, string rust, IEnumerable<string> keys, byte[] current, byte[]? defaults)
    {
        string directory = Path.Combine(root, "Backups", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Atomic.Write(Path.Combine(directory, "keys.cfg"), current);
        if (defaults != null) Atomic.Write(Path.Combine(directory, "keys_default.cfg"), defaults);
        Atomic.Write(Path.Combine(directory, "setup.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new SetupBackup(rust, keys.ToArray())));
        Atomic.Write(Path.Combine(directory, "restore.txt"), Encoding.UTF8.GetBytes(RustFiles.Restore(Path.Combine(directory, "keys.cfg"), keys)));
        return directory;
    }
}
