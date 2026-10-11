using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BlazorStudio.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorStudio.App;

public partial class MainWindow : Window
{
    private readonly ServiceProvider _services;
    private bool _closeConfirmed;

    public MainWindow()
    {
        var sc = new ServiceCollection();
        sc.AddWpfBlazorWebView();
#if DEBUG
        sc.AddBlazorWebViewDeveloperTools();
#endif
        sc.AddSingleton<IHostBridge>(new WpfHostBridge(this));
        sc.AddSingleton<SettingsService>();
        sc.AddSingleton<Localizer>();
        sc.AddSingleton<UiService>();
        sc.AddSingleton<Workspace>();
        sc.AddSingleton<RunService>();
        sc.AddSingleton<DebugService>();
        sc.AddSingleton<DesignActions>();
        sc.AddSingleton<NuGetService>();
        sc.AddSingleton<EfService>();
        sc.AddSingleton<ProfilerService>();
        sc.AddSingleton<TerminalService>();
        sc.AddSingleton<GitService>();
        sc.AddSingleton<UpdateService>();
        sc.AddSingleton<ProjectActions>();
        _services = sc.BuildServiceProvider();
        Resources.Add("services", _services);

        InitializeComponent();

        var settings = _services.GetRequiredService<SettingsService>();
        if (settings.Current.WindowWidth > 0)
        {
            Width = settings.Current.WindowWidth;
            Height = settings.Current.WindowHeight;
            if (settings.Current.WindowMaximized) WindowState = WindowState.Maximized;
        }

        WebView.BlazorWebViewInitialized += (_, e) =>
        {
            ((WpfHostBridge)_services.GetRequiredService<IHostBridge>()).Attach(e.WebView.CoreWebView2);
            e.WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 17, 17, 19);
        };

        SourceInitialized += (_, _) => ApplyTitleBarTheme(settings.Current.Theme != "light");
        Closing += OnClosing;
    }

    /// <summary>Dunkle bzw. helle Titelleiste passend zum Theme (Windows 11).</summary>
    public void ApplyTitleBarTheme(bool dark)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var useDark = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));
        var caption = dark ? 0x00131111 : 0x00EBE8E8; // 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        Background = new SolidColorBrush(dark ? Color.FromRgb(17, 17, 19) : Color.FromRgb(232, 232, 235));
    }

    /// <summary>Für das Auto-Update: ohne weitere Rückfrage beenden (ungespeicherte Änderungen wurden bereits geklärt).</summary>
    public void CloseForUpdate()
    {
        _closeConfirmed = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var settings = _services.GetRequiredService<SettingsService>();
        settings.Current.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            settings.Current.WindowWidth = Width;
            settings.Current.WindowHeight = Height;
        }
        settings.Save();

        if (_closeConfirmed) return;
        var ws = _services.GetRequiredService<Workspace>();
        var L = _services.GetRequiredService<Localizer>();
        if (ws.Documents.Any(d => d.IsDirty))
        {
            var r = MessageBox.Show(this, L["dlg.unsavedOnExit"], "Blazor Studio", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (r == MessageBoxResult.Yes) ws.SaveAll();
        }
        _closeConfirmed = true;
        _services.GetRequiredService<ProfilerService>().Dispose();
        _services.GetRequiredService<RunService>().StopAll();
        _ = _services.GetRequiredService<DebugService>().StopAsync();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
