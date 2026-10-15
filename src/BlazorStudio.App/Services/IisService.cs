using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlazorStudio.Deploy;

namespace BlazorStudio.App.Services;

public enum ServerHealth { Unknown, Checking, Online, Unauthorized, Offline, Error }

public sealed class ServerStatus
{
    public ServerHealth Health { get; set; }

    public AgentInfo? Info { get; set; }

    public string? Error { get; set; }

    public DateTime? Checked { get; set; }
}

/// <summary>Ein laufendes Deployment aus Studio heraus (Publish → ZIP → Upload je Server → Status abfragen).</summary>
public sealed class StudioDeployment
{
    public required string Version { get; init; }

    public required string Site { get; init; }

    /// <summary>publish, zip, upload, deploy, done.</summary>
    public string Step { get; set; } = "publish";

    public string? Server { get; set; }

    public double? Progress { get; set; }

    public bool Finished { get; set; }

    public bool Ok { get; set; }

    public string? Error { get; set; }

    public List<(string Server, DeploymentRecord Record)> Results { get; } = new();

    public CancellationTokenSource Cancel { get; } = new();
}

/// <summary>Verwaltet die IIS-Server (Agent) – Liste, Status im Hintergrund, Aufrufe und Deployments aus Studio.</summary>
public sealed class IisService : IDisposable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BlazorStudio.IisAgent");

    private readonly SettingsService _settings;
    private readonly RunService _run;
    private readonly Localizer _l;
    private readonly Dictionary<string, ServerStatus> _status = new();
    private readonly Dictionary<string, (string Key, AgentClient Client)> _clients = new();
    private readonly Timer _timer;
    private int _polling;

    private readonly Workspace _ws;
    private readonly UiService _ui;

    public IisService(SettingsService settings, RunService run, Localizer l, Workspace ws, UiService ui)
    {
        _settings = settings;
        _run = run;
        _l = l;
        _ws = ws;
        _ui = ui;
        _ws.RemoteSaved += doc => _ = UploadRemoteAsync(doc);
        // Erster Rundlauf kurz nach dem Start, dann alle 30 s – so gibt es immer einen aktuellen Status
        _timer = new Timer(_ => _ = PollAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
    }

    public event Action? Changed;

    public IReadOnlyList<IisServerEntry> Servers => _settings.Current.IisServers;

    public ServerStatus Status(IisServerEntry s)
    {
        lock (_status)
        {
            if (!_status.TryGetValue(s.Id, out var st)) _status[s.Id] = st = new ServerStatus();
            return st;
        }
    }

    public int OnlineCount => Servers.Count(s => Status(s).Health == ServerHealth.Online);

    public StudioDeployment? CurrentDeployment { get; private set; }

    private void Notify() => Changed?.Invoke();

    // ------------------------------------------------------------------
    // Serverliste
    // ------------------------------------------------------------------

    public void Save(IisServerEntry entry, string? newApiKey)
    {
        if (newApiKey is not null) entry.ProtectedApiKey = Protect(newApiKey);
        if (!Servers.Contains(entry)) _settings.Current.IisServers.Add(entry);
        DropClient(entry);
        _settings.Save();
        Notify();
        _ = RefreshAsync(entry);
    }

    public void Remove(IisServerEntry entry)
    {
        _settings.Current.IisServers.Remove(entry);
        DropClient(entry);
        lock (_status) _status.Remove(entry.Id);
        _settings.Save();
        Notify();
    }

    public void Move(IisServerEntry entry, int delta)
    {
        var list = _settings.Current.IisServers;
        var i = list.IndexOf(entry);
        var j = Math.Clamp(i + delta, 0, list.Count - 1);
        if (i < 0 || i == j) return;
        list.RemoveAt(i);
        list.Insert(j, entry);
        _settings.Save();
        Notify();
    }

    public static string? ApiKeyOf(IisServerEntry s) => Unprotect(s.ProtectedApiKey);

    private static string? Protect(string value)
    {
        if (value.Length == 0) return null;
        if (!OperatingSystem.IsWindows()) return "plain:" + value;
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    }

    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.StartsWith("plain:", StringComparison.Ordinal)) return value[6..];
        if (!OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }

    public AgentClient Client(IisServerEntry s)
    {
        var key = ApiKeyOf(s) ?? "";
        var signature = $"{s.Address}|{s.CertificateThumbprint}|{key}";
        lock (_clients)
        {
            if (_clients.TryGetValue(s.Id, out var c) && c.Key == signature) return c.Client;
            if (c.Client is not null) c.Client.Dispose();
            var client = new AgentClient(s.Address, key, s.CertificateThumbprint);
            _clients[s.Id] = (signature, client);
            return client;
        }
    }

    private void DropClient(IisServerEntry s)
    {
        lock (_clients)
        {
            if (_clients.Remove(s.Id, out var c)) c.Client.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Status
    // ------------------------------------------------------------------

    /// <summary>Diesen Server häufiger abfragen (für die Live-Kurven im geöffneten Fenster); null = aus.</summary>
    public IisServerEntry? Watched { get; set; }

    private DateTime _lastFull = DateTime.MinValue;

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            if (DateTime.UtcNow - _lastFull >= TimeSpan.FromSeconds(28))
            {
                _lastFull = DateTime.UtcNow;
                await Task.WhenAll(Servers.ToList().Select(RefreshAsync));
            }
            else if (Watched is { } w && Servers.Contains(w))
            {
                await RefreshAsync(w);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    /// <summary>Wird vom IIS-Fenster aufgerufen: schneller Takt für den gewählten Server.</summary>
    public void SetFastPolling(bool on) => _timer.Change(on ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30), on ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(30));

    public Task RefreshAllAsync() => Task.WhenAll(Servers.ToList().Select(RefreshAsync));

    public async Task RefreshAsync(IisServerEntry s)
    {
        var st = Status(s);
        if (st.Health == ServerHealth.Unknown)
        {
            st.Health = ServerHealth.Checking;
            Notify();
        }
        try
        {
            var client = Client(s);
            if (ApiKeyOf(s) is null)
            {
                await client.PingAsync();
                st.Health = ServerHealth.Unauthorized;
                st.Error = _l["iis.noKey"];
            }
            else
            {
                st.Info = await client.GetInfoAsync();
                st.Health = ServerHealth.Online;
                st.Error = null;
            }
        }
        catch (AgentException e)
        {
            st.Health = e.IsUnauthorized ? ServerHealth.Unauthorized : e.IsUnreachable ? ServerHealth.Offline : ServerHealth.Error;
            st.Error = e.Message;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            st.Health = ServerHealth.Offline;
            st.Error = e.Message;
        }
        st.Checked = DateTime.Now;
        Notify();
    }

    // ------------------------------------------------------------------
    // Deployment aus Studio
    // ------------------------------------------------------------------

    /// <summary>Versionsname aus der Vorlage: {version} Projektversion, {date} yyyyMMdd, {time} HHmm, {user} Benutzername.</summary>
    public static string ExpandVersion(string template, string? projectFile)
    {
        var version = "1.0.0";
        if (projectFile is not null && File.Exists(projectFile))
        {
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(projectFile), @"<(Version|VersionPrefix)>\s*([^<\s]+)\s*</");
            if (m.Success) version = m.Groups[2].Value;
        }
        var now = DateTime.Now;
        var s = (string.IsNullOrWhiteSpace(template) ? "{version}-{date}.{time}" : template)
            .Replace("{version}", version, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("HHmm", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{user}", Environment.UserName, StringComparison.OrdinalIgnoreCase);
        var clean = new string(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-' ? c : '-').ToArray()).Trim('.', '-', '_', '+');
        return clean.Length > 64 ? clean[..64] : clean;
    }

    // ------------------------------------------------------------------
    // Remote-Dateien (web.config, appsettings*.json, stdout-Logs) im Editor
    // ------------------------------------------------------------------

    private static string CacheDir(IisServerEntry s, string site, string appPath, string? sub = null)
    {
        static string Clean(string v) => string.Concat(v.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '\\' or '/' ? '_' : c));
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlazorStudio", "remote",
            Clean(s.Id), Clean(VersionStore.TargetFolderName(site, appPath)));
        return sub is null ? dir : Path.Combine(dir, sub);
    }

    /// <summary>Konfigurationsdatei vom Server holen und im Editor öffnen. Speichern (Strg+S) lädt sie wieder hoch.</summary>
    public async Task<bool> OpenRemoteConfigAsync(IisServerEntry s, string site, string appPath, string file, bool service = false)
    {
        var dir = service ? CacheDir(s, "svc_" + site, "/") : CacheDir(s, site, appPath);
        var path = Path.Combine(dir, file);
        var open = _ws.Documents.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        if (open is { IsDirty: true })
        {
            _ws.Activate(open);
            return true;
        }
        var content = service ? await Client(s).GetServiceConfigFileAsync(site, file) : await Client(s).GetConfigFileAsync(site, appPath, file);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content.Content);
        if (open is not null)
        {
            open.SetText(content.Content, ChangeOrigin.External);
            open.MarkSaved();
            open.LastWriteOnDisk = File.GetLastWriteTimeUtc(path);
        }
        var doc = _ws.Open(path);
        if (doc is null) return false;
        doc.Remote = new RemoteFile(s.Id, s.Display, site, appPath, file, IsService: service);
        _ws.NotifyChanged();
        if (content.Source == "new") _ui.ShowToast(_l.F("iis.cfg.newFile", file), ToastKind.Info, 5000);
        return true;
    }

    /// <summary>stdout-Log (Ende der Datei) im Editor anzeigen.</summary>
    public async Task<bool> OpenRemoteLogAsync(IisServerEntry s, string site, string appPath, string file)
    {
        var content = await Client(s).GetLogAsync(site, appPath, file);
        var dir = CacheDir(s, site, appPath, "logs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, file);
        await File.WriteAllTextAsync(path, content.Content);
        var open = _ws.Documents.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        if (open is not null)
        {
            open.SetText(content.Content, ChangeOrigin.External);
            open.MarkSaved();
        }
        var doc = _ws.Open(path);
        if (doc is null) return false;
        doc.Remote = new RemoteFile(s.Id, s.Display, site, appPath, file, IsLog: true);
        doc.RevealLine = Math.Max(1, doc.Text.Count(c => c == '\n'));
        _ws.NotifyChanged();
        return true;
    }

    private async Task UploadRemoteAsync(DocumentSession doc)
    {
        if (doc.Remote is not { } r) return;
        if (r.IsLog)
        {
            _ui.ShowToast(_l["iis.cfg.logReadOnly"], ToastKind.Warning, 4000);
            return;
        }
        var server = Servers.FirstOrDefault(x => x.Id == r.ServerId);
        var sent = doc.Text;
        try
        {
            if (server is null) throw new AgentException(_l.F("iis.cfg.serverGone", r.Server), null);
            var write = new SiteConfigWrite { Content = sent, Apply = true };
            var result = r.IsService
                ? await Client(server).SaveServiceConfigFileAsync(r.Site, r.File, write)
                : await Client(server).SaveConfigFileAsync(r.Site, r.AppPath, r.File, write);
            // Der Agent schaltet z. B. stdout ein – den gespeicherten Stand übernehmen
            if (result.Content != sent && doc.Text == sent)
            {
                doc.SetText(result.Content, ChangeOrigin.External);
                doc.MarkSaved();
                try { await File.WriteAllTextAsync(doc.Path, result.Content); } catch (IOException) { }
                doc.LastWriteOnDisk = File.GetLastWriteTimeUtc(doc.Path);
            }
            var msg = _l.F(result.Applied ? "iis.cfg.savedLive" : "iis.cfg.savedNext", r.File, r.Server);
            if (result.Recycled) msg += " " + _l["iis.cfg.recycled"];
            if (result.Notes.Count > 0) msg += " " + string.Join(" ", result.Notes);
            _ui.ShowToast(msg, ToastKind.Success, 6000);
        }
        catch (AgentException e)
        {
            // Wieder als ungespeichert markieren – lokal liegt die Datei nur im Zwischenspeicher
            if (doc.Text == sent) doc.MarkSavedAs("\0");
            _ws.NotifyChanged();
            _ui.ShowToast(_l.F("iis.cfg.uploadFailed", r.File, r.Server, e.Message), ToastKind.Error, 9000);
        }
    }

    /// <summary>Veröffentlicht das Projekt und rollt es nacheinander auf die Server aus (Abbruch beim ersten Fehler).</summary>
    public async Task<StudioDeployment> DeployAsync(string projectFile, IisDeployProfile profile, IReadOnlyList<IisServerEntry> servers, string version)
    {
        if (CurrentDeployment is { Finished: false }) throw new InvalidOperationException(_l["iis.deployBusy"]);
        var d = new StudioDeployment { Version = version, Site = profile.IsService ? profile.ServiceName : profile.Site };
        CurrentDeployment = d;
        Notify();
        var work = Path.Combine(Path.GetTempPath(), "BlazorStudio", "deploy-" + Guid.NewGuid().ToString("N")[..8]);
        var output = Path.Combine(work, "publish");
        var zip = Path.Combine(work, "package.zip");
        try
        {
            Directory.CreateDirectory(work);

            // Vorab: alle Ziele erreichbar und mit einem Agent, der den Pfad sicher umschaltet?
            foreach (var server in servers)
            {
                var ping = await Client(server).PingAsync(d.Cancel.Token);
                if (AgentDefaults.IsOlderThan(ping.Version, AgentDefaults.MinimumDeployVersion))
                    throw new InvalidOperationException(_l.F("iis.agentTooOld", server.Display, ping.Version, AgentDefaults.MinimumDeployVersion));
                // Ältere Agents kennen „target=service“ nicht und würden in eine IIS-Site gleichen Namens deployen
                if (profile.IsService && AgentDefaults.IsOlderThan(ping.Version, AgentDefaults.MinimumServiceVersion))
                    throw new InvalidOperationException(_l.F("iis.agentTooOld", server.Display, ping.Version, AgentDefaults.MinimumServiceVersion));
                _run.Write(_l.F("iis.log.agent", server.Display, ping.Version), OutputKind.Muted);
            }

            _run.Write(_l.F("iis.log.publish", Path.GetFileName(projectFile), version), OutputKind.Command);
            var args = new List<string> { "publish", projectFile, "-c", profile.Configuration, "-o", output, "-nologo" };
            if (!string.IsNullOrWhiteSpace(profile.RuntimeIdentifier))
            {
                args.AddRange(new[] { "-r", profile.RuntimeIdentifier, "--self-contained", profile.SelfContained ? "true" : "false" });
            }
            var code = await _run.ExecAsync(Path.GetDirectoryName(projectFile)!, args.ToArray());
            if (code != 0) throw new InvalidOperationException(_l["iis.publishFailed"]);
            d.Cancel.Token.ThrowIfCancellationRequested();

            d.Step = "zip";
            Notify();
            await Task.Run(() => System.IO.Compression.ZipFile.CreateFromDirectory(output, zip, System.IO.Compression.CompressionLevel.Optimal, false));
            var size = new FileInfo(zip).Length;
            _run.Write(_l.F("iis.log.package", ProfilerSize(size)), OutputKind.Muted);

            foreach (var server in servers)
            {
                d.Server = server.Display;
                d.Step = "upload";
                d.Progress = 0;
                Notify();
                _run.Write(_l.F("iis.log.upload", server.Display, server.Address), OutputKind.Command);
                var client = Client(server);
                var request = new DeployRequest
                {
                    Site = profile.IsService ? profile.ServiceName.Trim() : profile.Site.Trim(),
                    AppPath = profile.IsService ? "/" : DeployRequest.NormalizeAppPath(profile.AppPath),
                    Target = profile.IsService ? DeployTargets.Service : DeployTargets.Iis,
                    ServiceExe = profile.IsService ? profile.ServiceExe : null,
                    ServiceArguments = profile.IsService ? profile.ServiceArguments : null,
                    ServiceAccount = profile.IsService && !string.IsNullOrWhiteSpace(profile.ServiceAccount) ? profile.ServiceAccount : null,
                    ServiceStartType = profile.IsService && !string.IsNullOrWhiteSpace(profile.ServiceStartType) ? profile.ServiceStartType : null,
                    ServiceDisplayName = profile.IsService && !string.IsNullOrWhiteSpace(profile.ServiceDisplayName) ? profile.ServiceDisplayName : null,
                    Version = version,
                    CreateSite = profile.CreateSite,
                    Binding = profile.Binding,
                    Pool = profile.Pool,
                    HealthUrl = profile.HealthUrl,
                    Rollback = profile.Rollback,
                    Source = "Blazor Studio (" + Environment.UserName + ")",
                    Notes = profile.Notes,
                };
                var lastReport = DateTime.MinValue;
                var progress = new Progress<long>(sent =>
                {
                    d.Progress = size == 0 ? 1 : Math.Min(1, sent / (double)size);
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(150))
                    {
                        lastReport = DateTime.UtcNow;
                        Notify();
                    }
                });
                DeploymentRecord record;
                await using (var stream = File.OpenRead(zip))
                    record = await client.StartDeploymentAsync(request, stream, Path.GetFileName(projectFile) + "-" + version + ".zip", progress, d.Cancel.Token);

                d.Step = "deploy";
                d.Progress = null;
                d.Results.Add((server.Display, record));
                Notify();
                var shown = 0;
                record = await client.WaitForDeploymentAsync(record.Id, r =>
                {
                    foreach (var line in r.Log.Skip(shown))
                        _run.Write("  " + line.Text, line.Level switch { "error" => OutputKind.Error, "warn" => OutputKind.Warning, "ok" => OutputKind.Success, _ => OutputKind.Normal });
                    shown = r.Log.Count;
                    d.Results[^1] = (server.Display, r);
                    Notify();
                }, d.Cancel.Token);
                if (record.State != DeploymentState.Succeeded)
                    throw new InvalidOperationException(_l.F("iis.log.serverFailed", server.Display, record.Error ?? record.State.ToString()));
                _run.Write(_l.F("iis.log.serverOk", server.Display, version), OutputKind.Success);
                _ = RefreshAsync(server);
            }
            d.Ok = true;
        }
        catch (OperationCanceledException)
        {
            d.Error = _l["iis.cancelled"];
            _run.Write(d.Error, OutputKind.Warning);
        }
        catch (Exception e) when (e is AgentException or InvalidOperationException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            d.Error = e.Message;
            _run.Write(e.Message, OutputKind.Error);
        }
        finally
        {
            d.Step = "done";
            d.Finished = true;
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Notify();
        }
        return d;
    }

    public static string ProfilerSize(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes + " B",
    };

    public void Dispose()
    {
        _timer.Dispose();
        lock (_clients)
        {
            foreach (var c in _clients.Values) c.Client.Dispose();
            _clients.Clear();
        }
    }
}
