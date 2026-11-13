using Velopack;

namespace SiftDB.Host;

/// <summary>
/// The entry point. Velopack must run before anything else: during install, update and uninstall the installer starts
/// this exe with special arguments, and <see cref="VelopackApp.Run"/> handles them and exits. It also applies an update
/// that was downloaded earlier but not yet restarted into (design section 9).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetArgs(args).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
