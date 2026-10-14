using System.ComponentModel;
using System.Globalization;
using System.Text;
using BlazorStudio.Core.Build;
using BlazorStudio.Deploy.Docker;

namespace BlazorStudio.App.Services;

public sealed class DockerHostStatus
{
    public ServerHealth Health { get; set; }

    public DockerInfo? Info { get; set; }

    public string? Error { get; set; }

    public DateTime? Checked { get; set; }
}

/// <summary>Ergebnis eines docker-Aufrufs.</summary>
public sealed record DockerResult(int Code, string Output, string Error)
{
    public bool Ok => Code == 0;

    /// <summary>Fehlertext für Meldungen (letzte aussagekräftige Zeile von stderr bzw. stdout).</summary>
    public string Message
    {
        get
        {
            var text = Error.Trim().Length > 0 ? Error : Output;
            var lines = DockerCli.Lines(text);
            return lines.Count == 0 ? $"Exit-Code {Code}" : lines[^1];
        }
    }
}

/// <summary>Ein laufendes Docker-Deployment aus Studio (Image bauen/pushen → je Host Container ersetzen → prüfen).</summary>
public sealed class DockerDeployment
{
    public required string Version { get; init; }

    public required string Image { get; init; }

    public required string Container { get; init; }

    /// <summary>build, push, pull, run, check, done.</summary>
    public string Step { get; set; } = "build";

    public string? Host { get; set; }

    public bool Finished { get; set; }

    public bool Ok { get; set; }

    public string? Error { get; set; }

    /// <summary>Je Host: ok, rolledback, failed + Text.</summary>
    public List<(string Host, string State, string Detail)> Results { get; } = new();

    public CancellationTokenSource Cancel { get; } = new();
}

/// <summary>
/// Docker-Hosts (lokal oder per ssh:// / tcp://) über die Docker-CLI: Status, Container, Images,
/// Deployment mit Versionen (Image-Tags), Zurückschalten auf den vorherigen Container und Logs.
/// </summary>
public sealed class DockerService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly RunService _run;
    private readonly Localizer _l;
    private readonly Workspace _ws;
    private readonly Dictionary<string, DockerHostStatus> _status = new();
    private readonly Timer _timer;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private int _polling;

    public DockerService(SettingsService settings, RunService run, Localizer l, Workspace ws)
    {
        _settings = settings;
        _run = run;
        _l = l;
        _ws = ws;
        // Nur wenn Hosts eingetragen sind – docker info über SSH kostet ein paar hundert ms
        _timer = new Timer(_ => _ = PollAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
    }

    public event Action? Changed;

    public IReadOnlyList<DockerHostEntry> Hosts => _settings.Current.DockerHosts;

    public DockerDeployment? CurrentDeployment { get; private set; }

    public int OnlineCount => Hosts.Count(h => Status(h).Health == ServerHealth.Online);

    public DockerHostStatus Status(DockerHostEntry h)
    {
        lock (_status)
        {
            if (!_status.TryGetValue(h.Id, out var st)) _status[h.Id] = st = new DockerHostStatus();
            return st;
        }
    }

    private void Notify() => Changed?.Invoke();

    // ------------------------------------------------------------------
    // Hosts
    // ------------------------------------------------------------------

    public void SaveHost(DockerHostEntry entry)
    {
        var list = _settings.Current.DockerHosts;
        var i = list.FindIndex(x => x.Id == entry.Id);
        if (i >= 0) list[i] = entry;
        else list.Add(entry);
        _settings.Save();
        Notify();
        _ = RefreshAsync(entry);
    }

    public void RemoveHost(DockerHostEntry entry)
    {
        _settings.Current.DockerHosts.RemoveAll(x => x.Id == entry.Id);
        lock (_status) _status.Remove(entry.Id);
        _settings.Save();
        Notify();
    }

    private async Task PollAsync()
    {
        if (Hosts.Count == 0 || Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            await Task.WhenAll(Hosts.ToList().Select(RefreshAsync));
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    public Task RefreshAllAsync() => Task.WhenAll(Hosts.ToList().Select(RefreshAsync));

    public async Task RefreshAsync(DockerHostEntry h)
    {
        var st = Status(h);
        if (st.Health == ServerHealth.Unknown) { st.Health = ServerHealth.Checking; Notify(); }
        var r = await RunAsync(h, new[] { "info", "--format", "{{json .}}" }, timeout: TimeSpan.FromSeconds(20));
        st.Checked = DateTime.Now;
        DockerInfo? info = null;
        var json = DockerCli.Lines(r.Output).FirstOrDefault(l => l.StartsWith('{'));
        if (json is not null)
        {
            try { info = System.Text.Json.JsonSerializer.Deserialize<DockerInfo>(json, DockerJson.Options); }
            catch (System.Text.Json.JsonException) { }
        }
        if (r.Ok && info is not null && string.IsNullOrEmpty(info.ServerErrors?.FirstOrDefault()) && info.ServerVersion.Length > 0)
        {
            st.Health = ServerHealth.Online;
            st.Info = info;
            st.Error = null;
        }
        else
        {
            st.Health = r.Code == -1 ? ServerHealth.Error : ServerHealth.Offline;
            st.Info = null;
            st.Error = info?.ServerErrors?.FirstOrDefault() ?? r.Message;
        }
        Notify();
    }

    // ------------------------------------------------------------------
    // docker-CLI
    // ------------------------------------------------------------------

    /// <summary>docker [-H endpoint] args … ausführen. echo schreibt die Ausgabe zusätzlich ins Ausgabefenster.</summary>
    public async Task<DockerResult> RunAsync(DockerHostEntry? h, IEnumerable<string> args, bool echo = false, TimeSpan? timeout = null,
        string? workingDir = null, CancellationToken ct = default)
    {
        var all = DockerCli.HostArguments(h?.Endpoint).Concat(args).ToList();
        if (echo) _run.Write("❯ docker " + string.Join(' ', all.Select(Quote)), OutputKind.Command);
        var output = new StringBuilder();
        var error = new StringBuilder();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(30));
        using var runner = new ProcessRunner();
        runner.Output += (line, isError) =>
        {
            lock (output) (isError ? error : output).AppendLine(line);
            if (echo) _run.Write("  " + line, isError && LooksLikeError(line) ? OutputKind.Error : OutputKind.Normal);
        };
        try
        {
            var code = await runner.RunAsync("docker", all, workingDir ?? Path.GetTempPath(), null, cts.Token);
            if (cts.IsCancellationRequested && !ct.IsCancellationRequested) return new DockerResult(-2, output.ToString(), _l["dk.timeout"]);
            ct.ThrowIfCancellationRequested();
            return new DockerResult(code, output.ToString(), error.ToString());
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return new DockerResult(-1, "", _l["dk.cliMissing"]);
        }
    }

    /// <summary>docker schreibt Fortschritt nach stderr – nur echte Fehler rot zeigen.</summary>
    private static bool LooksLikeError(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.Contains("denied", StringComparison.OrdinalIgnoreCase);

    private static string Quote(string a) => a.Contains(' ') ? "\"" + a + "\"" : a;

    private async Task<DockerResult> MustAsync(DockerHostEntry? h, IEnumerable<string> args, bool echo = true, string? workingDir = null, CancellationToken ct = default)
    {
        var r = await RunAsync(h, args, echo, workingDir: workingDir, ct: ct);
        if (!r.Ok) throw new InvalidOperationException(r.Message);
        return r;
    }

    public async Task<List<DockerContainer>> GetContainersAsync(DockerHostEntry h, bool withStats)
    {
        var r = await RunAsync(h, new[] { "ps", "-a", "--format", "{{json .}}" }, timeout: TimeSpan.FromSeconds(30));
        if (!r.Ok) throw new InvalidOperationException(r.Message);
        var list = DockerCli.ParseJsonLines<DockerContainer>(r.Output);
        if (withStats && list.Any(c => c.IsRunning))
        {
            var s = await RunAsync(h, new[] { "stats", "--no-stream", "--format", "{{json .}}" }, timeout: TimeSpan.FromSeconds(30));
            if (s.Ok)
            {
                var stats = DockerCli.ParseJsonLines<DockerStats>(s.Output).ToDictionary(x => x.Name, StringComparer.Ordinal);
                foreach (var c in list) c.Stats = stats.GetValueOrDefault(c.Name);
            }
        }
        return list.OrderByDescending(c => c.IsManaged).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<List<DockerImage>> GetImagesAsync(DockerHostEntry h, string? repository = null)
    {
        var args = new List<string> { "images", "--format", "{{json .}}" };
        if (!string.IsNullOrWhiteSpace(repository)) args.Insert(1, repository);
        var r = await RunAsync(h, args, timeout: TimeSpan.FromSeconds(30));
        if (!r.Ok) throw new InvalidOperationException(r.Message);
        return DockerCli.ParseJsonLines<DockerImage>(r.Output)
            .Where(i => i.Repository != "<none>" || repository is null)
            .OrderByDescending(i => i.Created ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>start, stop, restart oder remove (rm -f).</summary>
    public async Task ContainerActionAsync(DockerHostEntry h, string name, string action)
    {
        var args = action switch
        {
            "start" => new[] { "start", name },
            "stop" => new[] { "stop", name },
            "restart" => new[] { "restart", name },
            "remove" => new[] { "rm", "-f", name },
            _ => throw new ArgumentException(action),
        };
        await MustAsync(h, args);
    }

    public Task RemoveImageAsync(DockerHostEntry h, string reference) => MustAsync(h, new[] { "rmi", reference });

    /// <summary>Letzte Log-Zeilen eines Containers im Editor zeigen.</summary>
    public async Task<bool> OpenLogsAsync(DockerHostEntry h, string container, int tail = 2000)
    {
        var r = await RunAsync(h, new[] { "logs", "--tail", tail.ToString(CultureInfo.InvariantCulture), "--timestamps", container }, timeout: TimeSpan.FromSeconds(60));
        if (!r.Ok && r.Output.Length == 0) throw new InvalidOperationException(r.Message);
        // docker logs gibt stdout und stderr des Containers getrennt aus – beides zeigen
        var text = r.Output + (r.Error.Length > 0 ? "\n" + r.Error : "");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlazorStudio", "docker-logs", Clean(h.Id));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Clean(container) + ".log");
        await File.WriteAllTextAsync(path, text);
        var open = _ws.Documents.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        if (open is not null)
        {
            open.SetText(text, ChangeOrigin.External);
            open.MarkSaved();
        }
        var doc = _ws.Open(path);
        if (doc is null) return false;
        doc.RevealLine = Math.Max(1, text.Count(c => c == '\n'));
        _ws.NotifyChanged();
        return true;
    }

    private static string Clean(string v) => string.Concat(v.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '\\' or '/' ? '_' : c));

    // ------------------------------------------------------------------
    // Container ersetzen, umschalten, zurückschalten
    // ------------------------------------------------------------------

    private async Task<bool> ExistsAsync(DockerHostEntry h, string container, CancellationToken ct = default) =>
        (await RunAsync(h, new[] { "container", "inspect", "--format", "{{.Name}}", container }, ct: ct, timeout: TimeSpan.FromSeconds(30))).Ok;

    private async Task<bool> IsRunningAsync(DockerHostEntry h, string container, CancellationToken ct = default)
    {
        var r = await RunAsync(h, new[] { "container", "inspect", "--format", "{{.State.Running}}", container }, ct: ct, timeout: TimeSpan.FromSeconds(30));
        return r.Ok && r.Output.Trim() == "true";
    }

    /// <summary>
    /// Ersetzt den Container durch einen neuen mit <paramref name="image"/>: alter Container wird gestoppt und als
    /// „name-previous“ aufgehoben, der neue gestartet und geprüft. Schlägt die Prüfung fehl, wird zurückgeschaltet.
    /// Rückgabe: ok, rolledback oder failed + Text.
    /// </summary>
    public async Task<(string State, string Detail)> ReplaceContainerAsync(DockerHostEntry h, string name, string image, DockerRunSpec spec,
        string version, string? project, string? healthPath, bool rollback, Action<string>? step = null, CancellationToken ct = default)
    {
        var previous = name + DockerCli.PreviousSuffix;
        var hadOld = await ExistsAsync(h, name, ct);
        if (hadOld)
        {
            if (await ExistsAsync(h, previous, ct)) await MustAsync(h, new[] { "rm", "-f", previous }, ct: ct);
            _run.Write(_l.F("dk.log.stopOld", name), OutputKind.Muted);
            await RunAsync(h, new[] { "stop", "-t", "20", name }, echo: true, ct: ct);
            await MustAsync(h, new[] { "rename", name, previous }, ct: ct);
            await DisableRestartAsync(h, previous);
        }

        step?.Invoke("run");
        var run = await RunAsync(h, DockerCli.RunArguments(name, image, spec, version, project, DateTimeOffset.UtcNow), echo: true, ct: ct);
        if (!run.Ok)
        {
            var detail = run.Message;
            await RestoreAsync(h, name, previous, hadOld);
            return (hadOld ? "rolledback" : "failed", _l.F("dk.runFailed", detail));
        }

        step?.Invoke("check");
        var (ok, info) = await CheckAsync(h, name, spec, healthPath, ct);
        if (ok)
        {
            _run.Write(_l.F("dk.log.healthy", name, info), OutputKind.Success);
            return ("ok", info);
        }

        _run.Write(_l.F("dk.log.unhealthy", name, info), OutputKind.Error);
        var logs = await RunAsync(h, new[] { "logs", "--tail", "40", name }, timeout: TimeSpan.FromSeconds(30));
        foreach (var line in DockerCli.Lines(logs.Output + "\n" + logs.Error).TakeLast(40)) _run.Write("  │ " + line, OutputKind.Muted);
        if (rollback && hadOld)
        {
            await RestoreAsync(h, name, previous, true);
            return ("rolledback", info);
        }
        return ("failed", info);
    }

    /// <summary>Neuen (fehlerhaften) Container entfernen und den vorherigen wieder unter seinem Namen starten.</summary>
    private async Task RestoreAsync(DockerHostEntry h, string name, string previous, bool hadOld)
    {
        await RunAsync(h, new[] { "rm", "-f", name }, echo: true);
        if (!hadOld) return;
        _run.Write(_l.F("dk.log.rollback", name), OutputKind.Warning);
        await RunAsync(h, new[] { "rename", previous, name }, echo: true);
        await RunAsync(h, new[] { "start", name }, echo: true);
        await RestoreRestartAsync(h, name);
    }

    /// <summary>Aufgehobener Container darf nach einem Docker-Neustart nicht wieder anlaufen (Port wäre doppelt belegt).</summary>
    private Task DisableRestartAsync(DockerHostEntry h, string container) =>
        RunAsync(h, new[] { "update", "--restart", "no", container }, timeout: TimeSpan.FromSeconds(30));

    /// <summary>Restart-Policy aus den gespeicherten Einstellungen (Label) wieder setzen.</summary>
    private async Task RestoreRestartAsync(DockerHostEntry h, string container)
    {
        var r = await RunAsync(h, new[] { "container", "inspect", "--format", "{{index .Config.Labels \"" + DockerCli.LabelRun + "\"}}", container }, timeout: TimeSpan.FromSeconds(30));
        var policy = (r.Ok ? DockerCli.DecodeSpec(r.Output.Trim())?.RestartPolicy : null) ?? "unless-stopped";
        if (policy != "no") await RunAsync(h, new[] { "update", "--restart", policy, container }, timeout: TimeSpan.FromSeconds(30));
    }

    /// <summary>Läuft der Container nach ein paar Sekunden noch, und antwortet die App (falls Port und Pfad gesetzt)?</summary>
    private async Task<(bool Ok, string Detail)> CheckAsync(DockerHostEntry h, string name, DockerRunSpec spec, string? healthPath, CancellationToken ct)
    {
        await Task.Delay(3000, ct);
        if (!await IsRunningAsync(h, name, ct)) return (false, _l["dk.notRunning"]);
        var url = DockerCli.HealthUrl(h.Endpoint, spec, healthPath);
        if (url is null) return (true, _l["dk.running"]);
        _run.Write(_l.F("dk.log.health", url), OutputKind.Muted);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var last = "";
        while (true)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                var code = (int)resp.StatusCode;
                last = $"HTTP {code}";
                // Jede Antwort unter 500 heißt: die App läuft (Anmeldung/404 auf „/“ ist kein Fehler des Deployments)
                if (code < 500) return (true, $"{url} → {last}");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                last = e.InnerException?.Message ?? e.Message;
            }
            if (!await IsRunningAsync(h, name, ct)) return (false, _l["dk.notRunning"] + " (" + last + ")");
            if (DateTime.UtcNow >= deadline) return (false, $"{url}: {last}");
            await Task.Delay(2000, ct);
        }
    }

    /// <summary>Bestehenden (von Studio verwalteten) Container auf ein anderes Image umstellen – gleiche Einstellungen.</summary>
    public async Task<(string State, string Detail)> SwitchAsync(DockerHostEntry h, DockerContainer container, string image, string? healthPath)
    {
        var labels = container.LabelMap;
        var spec = DockerCli.DecodeSpec(labels.GetValueOrDefault(DockerCli.LabelRun)) ?? new DockerRunSpec();
        _run.Write(_l.F("dk.log.switch", container.Name, container.Image, image), OutputKind.Command);
        return await ReplaceContainerAsync(h, container.Name, image, spec, DockerCli.TagOf(image), labels.GetValueOrDefault(DockerCli.LabelProject), healthPath, rollback: true);
    }

    /// <summary>Auf den vorherigen Container zurückschalten (tauscht „name“ und „name-previous“).</summary>
    public async Task RollbackAsync(DockerHostEntry h, string name)
    {
        var previous = name + DockerCli.PreviousSuffix;
        if (!await ExistsAsync(h, previous)) throw new InvalidOperationException(_l.F("dk.noPrevious", name));
        var temp = name + "-rollback-" + DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
        _run.Write(_l.F("dk.log.rollback", name), OutputKind.Command);
        await RunAsync(h, new[] { "stop", "-t", "20", name }, echo: true);
        await MustAsync(h, new[] { "rename", name, temp });
        await DisableRestartAsync(h, temp);
        await MustAsync(h, new[] { "rename", previous, name });
        var start = await RunAsync(h, new[] { "start", name }, echo: true);
        if (!start.Ok)
        {
            // Zurück auf den Stand vorher
            await RunAsync(h, new[] { "rename", name, previous }, echo: true);
            await RunAsync(h, new[] { "rename", temp, name }, echo: true);
            await RunAsync(h, new[] { "start", name }, echo: true);
            await RestoreRestartAsync(h, name);
            throw new InvalidOperationException(_l.F("dk.rollbackFailed", start.Message));
        }
        await RestoreRestartAsync(h, name);
        await MustAsync(h, new[] { "rename", temp, previous });
    }

    // ------------------------------------------------------------------
    // Deployment
    // ------------------------------------------------------------------

    public static DockerRunSpec SpecOf(DockerDeployProfile p) => new()
    {
        Ports = DockerCli.Lines(p.Ports),
        Environment = DockerCli.Lines(p.Environment),
        Volumes = DockerCli.Lines(p.Volumes),
        RestartPolicy = string.IsNullOrWhiteSpace(p.RestartPolicy) ? "unless-stopped" : p.RestartPolicy,
        Network = string.IsNullOrWhiteSpace(p.Network) ? null : p.Network.Trim(),
        ExtraArgs = DockerCli.SplitArguments(p.ExtraArgs),
    };

    /// <summary>Wie wird gebaut? Auto: Dockerfile neben der .csproj, sonst .NET SDK (PublishContainer).</summary>
    public static DockerBuildMode EffectiveMode(DockerDeployProfile p, string projectFile) =>
        p.BuildMode != DockerBuildMode.Auto ? p.BuildMode : DockerCli.FindDockerfile(projectFile) is not null ? DockerBuildMode.Dockerfile : DockerBuildMode.DotnetSdk;

    /// <summary>Prüft das Profil; null = in Ordnung, sonst Fehlertext.</summary>
    public string? Validate(DockerDeployProfile p, string projectFile)
    {
        if (!DockerCli.IsValidName(p.ContainerName)) return _l["dk.err.name"];
        if (!DockerCli.IsValidRepository(p.Repository)) return _l["dk.err.repo"];
        foreach (var port in DockerCli.Lines(p.Ports))
            if (!DockerCli.IsValidPort(port)) return _l.F("dk.err.port", port);
        foreach (var env in DockerCli.Lines(p.Environment))
            if (!env.Contains('=') || env.StartsWith('=')) return _l.F("dk.err.env", env);
        if (EffectiveMode(p, projectFile) == DockerBuildMode.Dockerfile && DockerCli.FindDockerfile(projectFile) is null) return _l["dk.err.noDockerfile"];
        return null;
    }

    public async Task<DockerDeployment> DeployAsync(string projectFile, DockerDeployProfile profile, IReadOnlyList<DockerHostEntry> hosts, string version)
    {
        if (CurrentDeployment is { Finished: false }) throw new InvalidOperationException(_l["dk.busy"]);
        var tag = DockerCli.ToTag(version);
        var registry = string.IsNullOrWhiteSpace(profile.Registry) ? null : profile.Registry.Trim().TrimEnd('/');
        var image = DockerCli.ImageReference(registry, profile.Repository.Trim(), tag);
        var d = new DockerDeployment { Version = version, Image = image, Container = profile.ContainerName.Trim() };
        CurrentDeployment = d;
        Notify();
        var ct = d.Cancel.Token;
        var spec = SpecOf(profile);
        var project = Path.GetFileNameWithoutExtension(projectFile);
        var mode = EffectiveMode(profile, projectFile);
        try
        {
            _run.Write(_l.F("dk.log.start", project, image, mode == DockerBuildMode.Dockerfile ? "Dockerfile" : ".NET SDK"), OutputKind.Command);
            if (registry is not null)
            {
                // Einmal bauen und pushen, dann auf jedem Host ziehen
                d.Step = "build";
                Notify();
                await BuildAsync(null, projectFile, profile, mode, registry, tag, image, push: true, ct);
            }

            foreach (var host in hosts)
            {
                ct.ThrowIfCancellationRequested();
                d.Host = host.Display;
                _run.Write(_l.F("dk.log.host", host.Display, host.IsLocal ? _l["dk.local"] : host.Endpoint), OutputKind.Command);
                if (registry is null)
                {
                    // Ohne Registry: direkt auf dem Ziel-Docker bauen (Build-Kontext geht über die Verbindung)
                    d.Step = "build";
                    Notify();
                    await BuildAsync(host, projectFile, profile, mode, null, tag, image, push: false, ct);
                }
                else
                {
                    d.Step = "pull";
                    Notify();
                    await MustAsync(host, new[] { "pull", image }, ct: ct);
                }

                var (state, detail) = await ReplaceContainerAsync(host, d.Container, image, spec, version, project, profile.HealthPath, profile.Rollback,
                    s => { d.Step = s; Notify(); }, ct);
                d.Results.Add((host.Display, state, detail));
                Notify();
                if (state != "ok")
                    throw new InvalidOperationException(_l.F(state == "rolledback" ? "dk.hostRolledBack" : "dk.hostFailed", host.Display, detail));
                _run.Write(_l.F("dk.log.hostOk", host.Display, d.Container, tag), OutputKind.Success);
                await PruneImagesAsync(host, profile, image);
                _ = RefreshAsync(host);
            }
            d.Ok = true;
        }
        catch (OperationCanceledException)
        {
            d.Error = _l["dk.cancelled"];
            _run.Write(d.Error, OutputKind.Warning);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            d.Error = e.Message;
            _run.Write(e.Message, OutputKind.Error);
        }
        finally
        {
            d.Step = "done";
            d.Finished = true;
            Notify();
        }
        return d;
    }

    private async Task BuildAsync(DockerHostEntry? host, string projectFile, DockerDeployProfile profile, DockerBuildMode mode,
        string? registry, string tag, string image, bool push, CancellationToken ct)
    {
        _run.Write(_l.F("dk.log.build", image, host is null ? _l["dk.local"] : host.Display), OutputKind.Command);
        if (mode == DockerBuildMode.Dockerfile)
        {
            var dockerfile = DockerCli.FindDockerfile(projectFile) ?? throw new InvalidOperationException(_l["dk.err.noDockerfile"]);
            var context = DockerCli.BuildContext(dockerfile, projectFile, _ws.Solution is { } sln ? Path.GetDirectoryName(sln.Path) : null);
            await MustAsync(host, new[] { "build", "-t", image, "-f", dockerfile, "--build-arg", "BUILD_CONFIGURATION=" + profile.Configuration, context },
                workingDir: context, ct: ct);
            if (push) await MustAsync(host, new[] { "push", image }, ct: ct);
            return;
        }

        // .NET SDK: dotnet publish /t:PublishContainer – lädt ins Docker von DOCKER_HOST oder pusht in die Registry
        var args = DockerCli.SdkPublishArguments(projectFile, profile.Repository.Trim(), tag, push ? registry : null, profile.Configuration, null);
        var env = new Dictionary<string, string>();
        if (host is { IsLocal: false }) env["DOCKER_HOST"] = host.Endpoint.Trim();
        _run.Write("❯ dotnet " + string.Join(' ', args.Select(Quote)), OutputKind.Command);
        using var runner = new ProcessRunner();
        var last = "";
        runner.Output += (line, isError) =>
        {
            last = line.Trim().Length > 0 ? line : last;
            var diag = ProcessRunner.ParseDiagnostic(line);
            _run.Write(line, diag?.Severity == DiagnosticSeverity.Error ? OutputKind.Error : diag?.Severity == DiagnosticSeverity.Warning ? OutputKind.Warning : OutputKind.Normal);
        };
        int code;
        try
        {
            code = await runner.RunAsync("dotnet", args, Path.GetDirectoryName(projectFile)!, env, ct);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(_l["run.dotnetMissing"]);
        }
        ct.ThrowIfCancellationRequested();
        if (code != 0) throw new InvalidOperationException(_l.F("dk.buildFailed", last));
    }

    /// <summary>Alte Images des Repositorys entfernen (die neuesten <see cref="DockerDeployProfile.KeepImages"/> bleiben).</summary>
    private async Task PruneImagesAsync(DockerHostEntry h, DockerDeployProfile profile, string current)
    {
        if (profile.KeepImages <= 0) return;
        try
        {
            var repo = DockerCli.RepositoryOf(current);
            var images = await GetImagesAsync(h, repo);
            var inUse = (await GetContainersAsync(h, false)).Select(c => c.Image).ToHashSet(StringComparer.Ordinal);
            foreach (var old in images.Where(i => i.Tag != "<none>").Skip(profile.KeepImages))
            {
                if (old.Reference == current || inUse.Contains(old.Reference) || inUse.Contains(old.Id)) continue;
                var r = await RunAsync(h, new[] { "rmi", old.Reference });
                if (r.Ok) _run.Write(_l.F("dk.log.pruned", old.Reference), OutputKind.Muted);
            }
        }
        catch (InvalidOperationException)
        {
            // Aufräumen ist optional
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
