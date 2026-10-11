using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace BlazorStudio.App.Services;

public sealed record UpdateInfo(string Version, string Tag, string Notes, string PageUrl, string? SetupUrl, string? SetupName, long SetupSize, DateTimeOffset Published);

/// <summary>
/// Prüft GitHub Releases (github.com/flattifabi/BlazingStudio) auf neue Versionen und installiert sie:
/// Setup herunterladen → still ausführen (/S /Launch) → App beenden; das Setup startet die neue Version.
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "flattifabi/BlazingStudio";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BlazorStudio";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly SettingsService _settings;
    private readonly HttpClient _http;

    public UpdateService(SettingsService settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BlazorStudio", CurrentVersion));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public event Action? Changed;

    /// <summary>Verfügbares Update (neuer als die laufende Version), sonst null.</summary>
    public UpdateInfo? Available { get; private set; }

    public bool Checking { get; private set; }

    /// <summary>Download-Fortschritt 0..1 während der Installation, sonst null.</summary>
    public double? DownloadProgress { get; private set; }

    public string? LastError { get; private set; }

    public static string CurrentVersion =>
        (typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0").Split('+')[0];

    /// <summary>Installationsordner, falls diese App über das Setup installiert wurde (nur dann ist ein Auto-Update möglich).</summary>
    public static string? InstalledLocation
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
            var loc = k?.GetValue("InstallLocation") as string;
            if (loc is null) return null;
            var here = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
            return string.Equals(Path.GetFullPath(loc).TrimEnd('\\'), here, StringComparison.OrdinalIgnoreCase) ? loc : null;
        }
    }

    public bool CanInstall => InstalledLocation is not null && Available?.SetupUrl is not null;

    public string ReleasesPage => $"https://github.com/{Repository}/releases";

    /// <summary>Beim Start: höchstens alle 6 Stunden, still im Hintergrund.</summary>
    public async Task CheckOnStartupAsync()
    {
        if (!_settings.Current.CheckForUpdates) return;
        if (DateTime.UtcNow - _settings.Current.LastUpdateCheck < CheckInterval)
        {
            // Bekanntes Update aus der letzten Prüfung trotzdem anzeigen
            if (_settings.Current.KnownUpdate is { } known && IsNewer(known, CurrentVersion) && known != _settings.Current.SkippedUpdate)
                await CheckAsync(manual: false);
            return;
        }
        await Task.Delay(TimeSpan.FromSeconds(4));
        await CheckAsync(manual: false);
    }

    /// <summary>Fragt das neueste Release ab. Liefert true, wenn ein Update verfügbar ist.</summary>
    public async Task<bool> CheckAsync(bool manual)
    {
        if (Checking) return Available is not null;
        Checking = true;
        LastError = null;
        Changed?.Invoke();
        try
        {
            // BLAZORSTUDIO_UPDATE_FEED: alternative Quelle im GitHub-Format (zum Testen des Update-Ablaufs)
            var feed = Environment.GetEnvironmentVariable("BLAZORSTUDIO_UPDATE_FEED") is { Length: > 0 } f ? f : $"https://api.github.com/repos/{Repository}/releases/latest";
            using var res = await _http.GetAsync(feed);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Available = null; // noch kein Release veröffentlicht
                return false;
            }
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var info = Parse(doc.RootElement);
            _settings.Current.LastUpdateCheck = DateTime.UtcNow;
            _settings.Current.KnownUpdate = info?.Version;
            _settings.Save();
            var newer = info is not null && IsNewer(info.Version, CurrentVersion);
            // Übersprungene Version nur bei automatischer Prüfung ausblenden
            Available = newer && (manual || info!.Version != _settings.Current.SkippedUpdate) ? info : null;
            return Available is not null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            LastError = e.Message;
            return false;
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    public static UpdateInfo? Parse(JsonElement r)
    {
        if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v', 'V');
        string? url = null, name = null;
        long size = 0;
        if (r.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? "";
                if (!n.StartsWith("BlazorStudio-Setup", StringComparison.OrdinalIgnoreCase) || !n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                url = a.GetProperty("browser_download_url").GetString();
                name = n;
                size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                break;
            }
        }
        var notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var page = r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        var published = r.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var d) ? d : DateTimeOffset.MinValue;
        return new UpdateInfo(version, tag, notes, page, url, name, size, published);
    }

    /// <summary>Semantischer Vergleich (1.10.0 &gt; 1.9.2; Vorabversionen zählen als älter).</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static (Version V, string Pre) Split(string s)
        {
            var dash = s.IndexOf('-');
            var core = dash < 0 ? s : s[..dash];
            var parts = core.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).Concat(new[] { 0, 0, 0 }).Take(4).ToArray();
            return (new Version(parts[0], parts[1], parts[2], parts[3]), dash < 0 ? "" : s[(dash + 1)..]);
        }
        var (a, ap) = Split(candidate.TrimStart('v', 'V'));
        var (b, bp) = Split(current.TrimStart('v', 'V'));
        if (a != b) return a > b;
        if (ap.Length == 0 && bp.Length > 0) return true;
        if (ap.Length > 0 && bp.Length == 0) return false;
        return string.CompareOrdinal(ap, bp) > 0;
    }

    public void Skip(UpdateInfo info)
    {
        _settings.Current.SkippedUpdate = info.Version;
        _settings.Save();
        Available = null;
        Changed?.Invoke();
    }

    /// <summary>Lädt das Setup herunter und startet es still; danach muss sich die App beenden.</summary>
    public async Task<bool> DownloadAndStartAsync(CancellationToken ct = default)
    {
        var info = Available;
        var dir = InstalledLocation;
        if (info?.SetupUrl is null || dir is null) return false;
        LastError = null;
        DownloadProgress = 0;
        Changed?.Invoke();
        try
        {
            var target = Path.Combine(Path.GetTempPath(), "BlazorStudio", "updates", info.SetupName ?? "BlazorStudio-Setup.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var res = await _http.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? info.SetupSize;
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(target);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                var lastReport = DateTime.MinValue;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0 && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(120))
                    {
                        lastReport = DateTime.UtcNow;
                        DownloadProgress = (double)done / total;
                        Changed?.Invoke();
                    }
                }
                if (total > 0 && done != total) throw new IOException("Download unvollständig.");
            }

            DownloadProgress = 1;
            Changed?.Invoke();
            // Setup wartet, bis diese Instanz beendet ist, installiert still und startet danach die neue Version
            var psi = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
            psi.ArgumentList.Add("/S");
            psi.ArgumentList.Add("/Launch");
            psi.ArgumentList.Add("/WaitPid=" + Environment.ProcessId);
            psi.ArgumentList.Add("/D=" + dir);
            Process.Start(psi);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            LastError = e.Message;
            DownloadProgress = null;
            Changed?.Invoke();
            return false;
        }
    }
}
