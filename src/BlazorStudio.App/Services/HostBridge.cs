using System.Diagnostics;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace BlazorStudio.App.Services;

/// <summary>Zugriff auf Fenster und Betriebssystem (Dialoge, Browser, WebView-Hostmapping).</summary>
public interface IHostBridge
{
    Task<string?> PickFolderAsync(string title, string? initial = null);

    /// <summary>filter im WPF-Format, z. B. "Projektmappen|*.sln;*.slnx".</summary>
    Task<string?> PickFileAsync(string title, string filter, string? initial = null);

    void OpenExternal(string url);

    void RevealInExplorer(string path);

    void SetClipboard(string text);

    /// <summary>App für ein Update beenden.</summary>
    void ExitForUpdate();

    /// <summary>Stellt den wwwroot-Ordner des Projekts unter https://project.assets/ bereit.</summary>
    void MapProjectAssets(string? folder);

    void SetTitle(string title);

    void SetDarkTitleBar(bool dark);

    Task InvokeOnUiAsync(Action action);
}

public sealed class WpfHostBridge(MainWindow window) : IHostBridge
{
    public const string AssetsHost = "project.assets";

    private CoreWebView2? _core;
    private string? _pendingFolder;

    public void Attach(CoreWebView2 core)
    {
        _core = core;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Links im Designer/Live-Vorschau nie in der App öffnen
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        if (_pendingFolder is not null) MapProjectAssets(_pendingFolder);
    }

    public Task<string?> PickFolderAsync(string title, string? initial = null)
    {
        string? result = null;
        window.Dispatcher.Invoke(() =>
        {
            var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
            if (initial is not null && Directory.Exists(initial)) dlg.InitialDirectory = initial;
            if (dlg.ShowDialog(window) == true) result = dlg.FolderName;
        });
        return Task.FromResult(result);
    }

    public Task<string?> PickFileAsync(string title, string filter, string? initial = null)
    {
        string? result = null;
        window.Dispatcher.Invoke(() =>
        {
            var dlg = new OpenFileDialog { Title = title, Filter = filter, Multiselect = false };
            if (initial is not null && Directory.Exists(initial)) dlg.InitialDirectory = initial;
            if (dlg.ShowDialog(window) == true) result = dlg.FileName;
        });
        return Task.FromResult(result);
    }

    public void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void ExitForUpdate() => window.Dispatcher.BeginInvoke(window.CloseForUpdate);

    public void SetClipboard(string text) => window.Dispatcher.Invoke(() =>
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    });

    public void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void MapProjectAssets(string? folder)
    {
        _pendingFolder = folder;
        if (_core is null) return;
        window.Dispatcher.Invoke(() =>
        {
            try { _core.ClearVirtualHostNameToFolderMapping(AssetsHost); } catch (ArgumentException) { }
            if (folder is not null && Directory.Exists(folder))
                _core.SetVirtualHostNameToFolderMapping(AssetsHost, folder, CoreWebView2HostResourceAccessKind.Allow);
        });
    }

    public void SetTitle(string title) => window.Dispatcher.Invoke(() => window.Title = title);

    public void SetDarkTitleBar(bool dark) => window.Dispatcher.Invoke(() => window.ApplyTitleBarTheme(dark));

    public Task InvokeOnUiAsync(Action action) => window.Dispatcher.InvokeAsync(action).Task;
}
