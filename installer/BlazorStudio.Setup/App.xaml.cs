using System.Windows;

namespace BlazorStudio.Setup
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // Stille Installation/Deinstallation: BlazorStudio-Setup.exe /S [/D=<Pfad>] [/NoDesktop] – Uninstall.exe /S
            if (Installer.TryRunSilent(e.Args, out var exitCode))
            {
                Shutdown(exitCode);
                return;
            }
            new MainWindow().Show();
        }
    }
}
