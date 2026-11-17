using RustMidiManager;
using System.IO;
using System.Text;

internal static class Program
{
    static int count;
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        count++;
        Console.WriteLine("PASS: " + name);
    }

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    [STAThread]
    static int Main(string[] args)
    {
        string root = Path.Combine(args.Length == 0 ? Path.GetTempPath() : args[0], "rmm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Run(root);
            Console.WriteLine($"Passed {count} checks.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, recursive: true); }
    }

    static void Run(string root)
    {
        string steam = Path.Combine(root, "Steam");
        string secondary = Path.Combine(root, "Другой диск");
        string rust = Path.Combine(secondary, "steamapps", "common", "Custom Rust");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(rust, "RustClient_Data"));
        File.WriteAllText(Path.Combine(rust, "RustClient.exe"), "");
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\"\n{\n\"1\"\n{\n\"path\" \"" + secondary.Replace("\\", "\\\\") + "\"\n}\n}");
        File.WriteAllText(Path.Combine(secondary, "steamapps", "appmanifest_252490.acf"), "\"installdir\" \"Custom Rust\"");
        Check(RustFiles.Discover([steam]) == rust, "Steam VDF custom library and manifest");
        Check(!RustFiles.IsInstall(secondary), "reject incomplete installation");

        byte[] fixture = Convert.FromHexString("4D546864000000060000000101E04D54726B0000001400FF510307A12000903C648360803C0000FF2F00");
        var parsed = MidiFile.Read(fixture);
        Check(parsed.Tracks == 1 && parsed.Messages.Count == 2 && Math.Abs(parsed.Duration - .5) < 1e-6, "SMF tempo, timing and notes");
        Check(Throws<InvalidDataException>(() => MidiFile.Read(fixture[..^1])), "reject truncated MIDI");
        Check(Throws<InvalidDataException>(() => MidiFile.Read([0, 1, 2])), "reject invalid header");

        var library = new MusicLibrary(Path.Combine(root, "Data"));
        string incoming = Path.Combine(root, "Incoming");
        string nested = Path.Combine(incoming, "nested");
        Directory.CreateDirectory(nested);
        string original = Path.Combine(nested, "Музыка; ' ♫.midi");
        File.WriteAllBytes(original, fixture);
        File.WriteAllBytes(Path.Combine(nested, "Duplicate.MID"), fixture);
        byte[] second = fixture.ToArray();
        second[32] = 81;
        string alternative = Path.Combine(nested, "Другой MIDI.mid");
        File.WriteAllBytes(alternative, second);
        File.WriteAllText(Path.Combine(nested, "Bad.mid"), "bad");
        Check(MainWindow.ImportFilesToLibrary([incoming], [], library.Media, false).Added.Count == 0, "non-recursive import skips nested files");
        var batch = MainWindow.ImportFilesToLibrary([incoming], [], library.Media, true);
        Check(batch.Added.Count == 2 && batch.Errors.Count == 1 && batch.Duplicates == 1, "recursive batch tolerates duplicates and invalid MIDI");
        var song = MusicLibrary.ReadSong(original, out _);
        var other = MusicLibrary.ReadSong(alternative, out _);
        Check(song.Title == "Музыка; ' ♫" && song.FileName.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.'), "Unicode title and safe Rust filename");
        library.Data.Songs.AddRange([song, other]);
        library.Data.Selected = song.Id;
        library.Data.Rust = rust;
        library.Data.Volume = 31;
        library.Data.Bindings["f6"] = song.Id;
        library.Data.Bindings["f7"] = other.Id;
        library.Data.ResetKey = "f8";
        library.Data.CycleKey = "f9";
        library.Data.CyclePlaylist = "Плейлист";
        library.Data.Playlists.Add(new Playlist { Name = "Плейлист", Songs = [other.Id, song.Id] });
        library.Save();
        var reopened = new MusicLibrary(library.Root).Data;
        Check(reopened.Selected == song.Id && reopened.Volume == 31 && reopened.Rust == rust && reopened.Playlists[0].Songs[0] == other.Id,
            "catalog, selection, playlist order and settings persist");
        library.PublishSongs(library.Data.Songs, rust);
        Check(File.ReadAllBytes(Path.Combine(rust, "instruments", song.FileName)).SequenceEqual(fixture) && File.Exists(original), "sync copies and preserves original");

        BindingPlan direct = BindingPlan.Create(reopened, false, true);
        var plan = BindingPlan.Create(reopened, true);
        Check(plan.ConsoleText.Contains("bind f6 instruments.overrideautoplay " + song.Stem) &&
            plan.ConsoleText.Contains("bind f8 instruments.clearautoplayoverride") && plan.ConsoleText.EndsWith("writecfg"), "autoplay commands, reset and writecfg");
        Check(plan.Commands["f9"] == "~" + RustFiles.Autoplay(other) + ";" + RustFiles.Autoplay(song), "documented cycle syntax preserves playlist order");
        Check(direct.Commands["f6"] == "instruments.playrecording " + song.Stem && !direct.Commands.ContainsKey("f8"), "documented direct playback fallback");
        Check(!plan.ConsoleText.Contains(song.Title) && Throws<ArgumentException>(() => RustFiles.Bind("f6;quit", "instruments.clearautoplayoverride")) &&
            Throws<ArgumentException>(() => RustFiles.Bind("f6", "x\"\nquit")), "reject command injection");
        reopened.ResetKey = "f6";
        Check(Throws<InvalidOperationException>(() => BindingPlan.Create(reopened, true)), "detect assignment collision");

        string cfg = Path.Combine(rust, "cfg");
        Directory.CreateDirectory(cfg);
        string keys = Path.Combine(cfg, "keys.cfg");
        byte[] saved = Encoding.UTF8.GetBytes("bind f6 \"chat.say test\"\nbind [leftshift+f7] inventory.toggle\nbind mouse1 +attack2\n");
        byte[] defaults = Encoding.UTF8.GetBytes("bind_default f8 forward\n");
        File.WriteAllBytes(keys, saved);
        var existing = RustFiles.Existing(keys);
        Check(RustFiles.Conflicts(existing.Keys.Single(key => key.Contains('+')), "f7") && existing["f6"].Contains("\"chat.say test\""), "parse quoted commands and chord conflict");
        string backup = SetupBackup.Make(library.Root, rust, ["f6", "f7", "f8"], saved, defaults);
        string restore = RustFiles.Restore(Path.Combine(backup, "keys.cfg"), ["f6", "f7", "f8"]);
        Check(restore.Contains("bind f6 \"chat.say test\"") && restore.Contains("bind f7 \"\"") &&
            restore.Contains("bind f8 forward") && restore.EndsWith("writecfg") && !restore.Contains("mouse1"), "targeted restore with original quoting, vacant key and default");
        Check(File.ReadAllBytes(keys).SequenceEqual(saved) && File.ReadAllBytes(Path.Combine(backup, "keys.cfg")).SequenceEqual(saved), "snapshot integrity and untouched game config");
        File.WriteAllText(Path.Combine(library.Root, "library.json"), "{ invalid");
        var recovered = new MusicLibrary(library.Root);
        Check(recovered.ReadyAfterRecovery(), "corrupt catalog recovery preserves original");
    }
}

internal static class Checks
{
    public static bool ReadyAfterRecovery(this MusicLibrary library) =>
        library.Data.Songs.Count == 0 && Directory.GetFiles(library.Root, "library-damaged-*.json").Length == 1;
}
