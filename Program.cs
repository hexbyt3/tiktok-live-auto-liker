using Velopack;

namespace TikTokLiveAutoLiker;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Velopack has to see the installer's --veloapp-* phases before any UI exists.
        //
        // Never fatal. Velopack rewrites Update.exe to stage an update, and on a machine with
        // Defender's ASR rule "block executables that don't meet a prevalence or age criterion"
        // in force, that freshly written exe gets blocked — applying the update then throws
        // Win32Exception (5) Access denied straight out of Main and the app stops launching
        // altogether. This bricked ShoeBay on 2026-08-15. There's no logger in this app, so the
        // note goes beside the install where it can be found.
        try
        {
            VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TikTokLiveAutoLiker");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "updater-errors.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Velopack startup hook failed — " +
                    $"continuing on the installed version{Environment.NewLine}{ex}{Environment.NewLine}");
            }
            catch
            {
                // a failure to log a failure must not stop the app either
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
