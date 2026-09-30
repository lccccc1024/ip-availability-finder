using Microsoft.UI.Xaml;

namespace PingCandidateFinder.WinUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, error) =>
        {
            Log(error.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            string[] command = Environment.GetCommandLineArgs();
            if (command.Length >= 3 && command[1] == "--preview")
            {
                if (command.Length >= 6 && int.TryParse(command[4], out int width)
                    && int.TryParse(command[5], out int height))
                    _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
                string scenario = command.Length >= 4 ? command[3] : "";
                ((MainWindow)_window).SchedulePreview(command[2], scenario is "results" or "full", scenario == "full");
            }
            _window.Activate();
        }
        catch (Exception error)
        {
            Log(error);
            throw;
        }
    }

    internal static void Log(Exception error)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PingCandidateFinder");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "error.log"), error.ToString());
        }
        catch { }
    }
}
