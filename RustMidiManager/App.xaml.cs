using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;

namespace RustMidiManager;

public partial class App : Application
{
    Mutex? instance;
    internal static string DataDirectory { get; private set; } = MusicLibrary.DefaultRoot;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (e.Args.Length == 2 && e.Args[0] == "--data-dir")
                DataDirectory = Path.GetFullPath(e.Args[1]);
            string identifier = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())));
            instance = new Mutex(true, "Local\\RustMidiManager_" + identifier, out bool first);
            if (!first)
            {
                MessageBox.Show("Приложение уже открыто. Проверьте панель задач и область уведомлений.", "Rust MIDI Manager");
                Shutdown();
                return;
            }
            DispatcherUnhandledException += (_, args) =>
            {
                if (args.Exception is not (IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                    or InvalidOperationException or System.Runtime.InteropServices.ExternalException or System.Text.Json.JsonException)) return;
                MessageBox.Show(args.Exception.Message, "Rust MIDI Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show("Не удалось открыть приложение.\n\n" + error.Message, "Rust MIDI Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
