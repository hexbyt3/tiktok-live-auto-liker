using Velopack;

namespace TikTokLiveAutoLiker;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Velopack has to see the installer's --veloapp-* phases before any UI exists.
        VelopackApp.Build().Run();

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
