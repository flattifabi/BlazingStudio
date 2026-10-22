using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlazorStudio.Deploy;

namespace BlazorStudio.Agent;

/// <summary>
/// Zertifikate auf dem Server: Windows-Zertifikatsspeicher (mit IIS-Bindungen), nginx-Konfiguration,
/// PFX/PEM-Dateien von Diensten (Kestrel) und win-acme-Erneuerungen. Erneuern/Anlegen läuft über wacs.exe.
/// Passwörter werden nie zurückgegeben oder protokolliert.
/// </summary>
public sealed partial class CertificateService(AgentOptions options, IisManager iis, ServiceManager services, ILogger<CertificateService> log)
{
    private static readonly JsonDocumentOptions JsonRead = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ConcurrentDictionary<string, CertificateJob> _jobs = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private (DateTime At, WinAcmeTask? Task) _taskCache;

    private sealed record WinAcmeTask(string Name, string State, string Exe, DateTime? LastRun, long? LastResult, DateTime? NextRun);

    // ------------------------------------------------------------------
    // Übersicht
    // ------------------------------------------------------------------

    public async Task<CertificateOverview> ScanAsync(bool probe, CancellationToken ct = default)
    {
        await _scanLock.WaitAsync(ct);
        try
        {
            return await ScanCoreAsync(probe, ct);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task<CertificateOverview> ScanCoreAsync(bool probe, CancellationToken ct)
    {
        var o = new CertificateOverview { MachineName = Environment.MachineName, ScannedAt = DateTime.UtcNow };
        o.WinAcme = await WinAcmeAsync(o.Notes, ct);

        // win-acme-Erneuerungen
        foreach (var file in RenewalFiles(o.WinAcme))
        {
            try
            {
                var r = WinAcmeRenewalParser.Parse(await File.ReadAllTextAsync(file, ct), file);
                r.Due = WinAcmeRenewalParser.Due(r, o.WinAcme.RenewalDays, o.WinAcme.RenewalMinimumValidDays);
                o.Renewals.Add(r);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
                o.Notes.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
        }

        // Windows-Zertifikatsspeicher
        foreach (var storeName in new[] { "My", "WebHosting" })
        {
            try
            {
                using var store = new X509Store(storeName, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (var cert in store.Certificates)
                {
                    using (cert)
                    {
                        if (!cert.HasPrivateKey || !IsServerCertificate(cert)) continue;
                        var info = ToInfo(cert);
                        info.Source = "store:" + storeName;
                        o.Certificates.Add(info);
                    }
                }
            }
            catch (CryptographicException) { }
        }

        // IIS-Bindungen
        if (IisManager.IisInstalled)
        {
            try
            {
                foreach (var b in iis.GetCertificateBindings())
                {
                    var target = o.Certificates.FirstOrDefault(c => c.Source.StartsWith("store:") && c.Thumbprint.Equals(b.Thumbprint, StringComparison.OrdinalIgnoreCase));
                    if (target is null) continue;
                    target.Usages.Add(new CertificateUsage
                    {
                        Kind = "iis", Target = b.Site, Detail = b.Binding,
                        Endpoint = $"{(b.Host.Length > 0 ? b.Host : "localhost")}:{b.Port}",
                    });
                }
            }
            catch (Exception e) when (e is IisUnavailableException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                o.Notes.Add("IIS-Bindungen: " + e.Message);
            }
        }

        // Dateien: nginx, Dienste (Kestrel), win-acme-Ablage, zusätzlich eingetragene
        var files = new Dictionary<string, (List<CertificateUsage> Usages, List<string?> Passwords)>(StringComparer.OrdinalIgnoreCase);
        void AddFile(string path, CertificateUsage? usage, string? password = null)
        {
            string full;
            try
            {
                full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }
            if (!files.TryGetValue(full, out var entry)) files[full] = entry = (new(), new());
            if (usage is not null) entry.Usages.Add(usage);
            if (password is not null) entry.Passwords.Add(password);
        }

        ScanNginx(o, AddFile);
        ScanServices(o, AddFile);
        foreach (var r in o.Renewals)
        foreach (var path in r.StorePaths)
        {
            if (!Directory.Exists(path)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(path))
                {
                    var n = Path.GetFileName(f).ToLowerInvariant();
                    if (n.EndsWith("-crt.pem") || n.EndsWith(".pfx") || n.EndsWith(".p12"))
                        if (r.Hosts.Count == 0 || r.Hosts.Any(h => n.StartsWith(h.Replace("*", "_"), StringComparison.OrdinalIgnoreCase)) || r.Name.Length > 0 && n.StartsWith(SafeFileName(r.Name.Split(" @ ")[0]).ToLowerInvariant()))
                            AddFile(f, null);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var f in options.CertificateFiles) AddFile(f, new CertificateUsage { Kind = "file", Target = "Agent-Einstellung", Detail = f });

        foreach (var (path, entry) in files)
        {
            var info = LoadFile(path, entry.Passwords);
            info.Usages.AddRange(entry.Usages);
            o.Certificates.Add(info);
        }

        // Erneuerungen zuordnen
        foreach (var c in o.Certificates)
        {
            var r = o.Renewals.FirstOrDefault(r => r.Thumbprints.Contains(c.Thumbprint, StringComparer.OrdinalIgnoreCase))
                    ?? (c.Path is { } p ? o.Renewals.FirstOrDefault(r => r.StorePaths.Any(sp => p.StartsWith(Path.GetFullPath(sp), StringComparison.OrdinalIgnoreCase))
                                                                         && r.Hosts.Intersect(c.DnsNames, StringComparer.OrdinalIgnoreCase).Any()) : null);
            if (r is null) continue;
            c.RenewalId = r.Id;
            if (c.Error is not null && c.NotAfter == default)
            {
                // PFX mit unbekanntem Passwort: Angaben aus win-acme übernehmen
                c.DnsNames = r.Hosts.ToList();
                c.NotAfter = r.Expires ?? default;
                c.NotBefore = r.LastSuccessAt ?? default;
                c.Thumbprint = r.Thumbprints.FirstOrDefault() ?? "";
            }
            c.Usages.Add(new CertificateUsage { Kind = "winacme", Target = r.Id, Detail = r.Name });
        }

        // win-acme legt dasselbe Zertifikat oft mehrfach ab (Speicher, -crt.pem, -chain.pem): unbenutzte Doppel ausblenden
        static bool Used(CertificateInfo c) => c.Usages.Any(u => u.Kind != "winacme");
        o.Certificates.RemoveAll(c => c.Source == "file" && !Used(c) && c.Thumbprint.Length > 0 &&
            o.Certificates.Any(other => !ReferenceEquals(other, c) && other.Thumbprint.Equals(c.Thumbprint, StringComparison.OrdinalIgnoreCase) && (Used(other) || other.Source != "file")));

        if (probe) await ProbeAsync(o.Certificates, ct);
        o.Certificates = o.Certificates.OrderBy(c => c.NotAfter == default ? DateTime.MaxValue : c.NotAfter).ToList();
        return o;
    }

    private static bool IsServerCertificate(X509Certificate2 cert)
    {
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return eku is null || eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1");
    }

    private static CertificateInfo ToInfo(X509Certificate2 cert)
    {
        var info = new CertificateInfo
        {
            Thumbprint = cert.Thumbprint.ToUpperInvariant(),
            Subject = cert.Subject,
            Issuer = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: true) is { Length: > 0 } issuer ? issuer : cert.Issuer,
            NotBefore = cert.NotBefore.ToUniversalTime(),
            NotAfter = cert.NotAfter.ToUniversalTime(),
            HasPrivateKey = cert.HasPrivateKey,
        };
        try
        {
            info.FriendlyName = OperatingSystem.IsWindows() && cert.FriendlyName is { Length: > 0 } fn ? fn : null;
        }
        catch (CryptographicException) { }
        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") continue;
            try
            {
                info.DnsNames.AddRange(new X509SubjectAlternativeNameExtension(ext.RawData, ext.Critical).EnumerateDnsNames());
            }
            catch (CryptographicException) { }
        }
        if (info.DnsNames.Count == 0 && cert.GetNameInfo(X509NameType.DnsName, false) is { Length: > 0 } cn) info.DnsNames.Add(cn);
        info.DnsNames = info.DnsNames.Select(n => n.ToLowerInvariant()).Distinct().ToList();
        return info;
    }

    private static CertificateInfo LoadFile(string path, List<string?> passwords)
    {
        var fail = new CertificateInfo { Source = "file", Path = path };
        if (!File.Exists(path))
        {
            fail.Error = "Datei fehlt";
            return fail;
        }
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".pfx" or ".p12")
            {
                foreach (var pw in passwords.Append(null).Append("").Distinct())
                {
                    try
                    {
                        using var cert = X509CertificateLoader.LoadPkcs12FromFile(path, pw, X509KeyStorageFlags.EphemeralKeySet);
                        var info = ToInfo(cert);
                        info.Source = "file";
                        info.Path = path;
                        return info;
                    }
                    catch (CryptographicException) { }
                }
                fail.Error = "PFX passwortgeschützt";
                return fail;
            }
            var text = File.ReadAllText(path);
            if (text.Contains("-----BEGIN CERTIFICATE", StringComparison.Ordinal))
            {
                var all = new X509Certificate2Collection();
                all.ImportFromPem(text);
                try
                {
                    var leaf = all.Cast<X509Certificate2>().FirstOrDefault() ?? throw new CryptographicException("Kein Zertifikat in der Datei.");
                    var info = ToInfo(leaf);
                    info.HasPrivateKey = text.Contains("PRIVATE KEY", StringComparison.Ordinal);
                    info.Source = "file";
                    info.Path = path;
                    return info;
                }
                finally
                {
                    foreach (var c in all) c.Dispose();
                }
            }
            using (var der = X509CertificateLoader.LoadCertificateFromFile(path))
            {
                var info = ToInfo(der);
                info.Source = "file";
                info.Path = path;
                return info;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            fail.Error = e.Message;
            return fail;
        }
    }

    private static string SafeFileName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '*' ? '_' : c)).Trim();

    // ------------------------------------------------------------------
    // nginx
    // ------------------------------------------------------------------

    /// <summary>nginx.exe finden: Einstellung, laufender Prozess, übliche Ordner.</summary>
    public string? NginxExe()
    {
        var configured = options.NginxPath.Trim();
        if (configured.Length > 0)
        {
            var path = Environment.ExpandEnvironmentVariables(configured);
            if (path.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(path);
                var exe = dir is null ? null : new[] { Path.Combine(dir, "nginx.exe"), Path.Combine(Path.GetDirectoryName(dir) ?? dir, "nginx.exe") }.FirstOrDefault(File.Exists);
                return exe;
            }
            if (Directory.Exists(path)) path = Path.Combine(path, "nginx.exe");
            return File.Exists(path) ? path : null;
        }
        foreach (var p in Process.GetProcessesByName("nginx"))
        {
            using (p)
            {
                try
                {
                    if (p.MainModule?.FileName is { } file && File.Exists(file)) return file;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        var candidates = new List<string> { @"C:\nginx\nginx.exe", @"C:\Program Files\nginx\nginx.exe", @"C:\tools\nginx\nginx.exe" };
        try
        {
            candidates.AddRange(Directory.EnumerateDirectories(@"C:\", "nginx*").Select(d => Path.Combine(d, "nginx.exe")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return candidates.FirstOrDefault(File.Exists);
    }

    private string? NginxConf(string? exe)
    {
        var configured = Environment.ExpandEnvironmentVariables(options.NginxPath.Trim());
        if (configured.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) && File.Exists(configured)) return configured;
        if (exe is null) return null;
        var conf = Path.Combine(Path.GetDirectoryName(exe)!, "conf", "nginx.conf");
        return File.Exists(conf) ? conf : null;
    }

    private void ScanNginx(CertificateOverview o, Action<string, CertificateUsage?, string?> addFile)
    {
        o.NginxExe = NginxExe();
        if (NginxConf(o.NginxExe) is not { } main) return;
        var confDir = Path.GetDirectoryName(main)!;
        var queue = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue(main);
        while (queue.Count > 0 && seen.Count < 100)
        {
            var file = queue.Dequeue();
            if (!seen.Add(file)) continue;
            NginxConfigParser.Result parsed;
            try
            {
                parsed = NginxConfigParser.Parse(File.ReadAllText(file));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                o.Notes.Add($"nginx: {file}: {e.Message}");
                continue;
            }
            o.NginxConfigs.Add(file);
            foreach (var inc in parsed.Includes)
                foreach (var f in Expand(inc, confDir))
                    queue.Enqueue(f);
            foreach (var cert in parsed.DefaultCertificates)
                addFile(Resolve(cert, confDir), new CertificateUsage { Kind = "nginx", Target = Path.GetFileName(file), Detail = "http (Standard)" }, null);
            foreach (var server in parsed.Servers)
            foreach (var cert in server.Certificates)
            {
                var host = server.Names.FirstOrDefault(n => !n.StartsWith('~') && !n.Contains('*'));
                var port = server.SslPorts.Count > 0 ? server.SslPorts[0] : 443;
                addFile(Resolve(cert, confDir), new CertificateUsage
                {
                    Kind = "nginx", Target = Path.GetFileName(file),
                    Detail = server.Names.Count > 0 ? string.Join(" ", server.Names) : "server",
                    Endpoint = host is null ? null : $"{host}:{port}",
                }, null);
            }
        }
    }

    private static string Resolve(string path, string baseDir) =>
        Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path.Replace('/', Path.DirectorySeparatorChar));

    private static IEnumerable<string> Expand(string pattern, string baseDir)
    {
        var full = Resolve(pattern, baseDir);
        var dir = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        if (dir is null || !Directory.Exists(dir)) return Array.Empty<string>();
        try
        {
            return name.Contains('*') || name.Contains('?') ? Directory.GetFiles(dir, name).OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : File.Exists(full) ? new[] { full } : Array.Empty<string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    // ------------------------------------------------------------------
    // Dienste (Kestrel-Zertifikate in appsettings*.json)
    // ------------------------------------------------------------------

    private void ScanServices(CertificateOverview o, Action<string, CertificateUsage?, string?> addFile)
    {
        List<ServiceInfo> list;
        try
        {
            list = services.List(true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            o.Notes.Add("Dienste: " + e.Message);
            return;
        }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var svc in list)
        {
            var dir = svc.Directory;
            if (dir.Length == 0 || !Directory.Exists(dir) || (windows.Length > 0 && dir.StartsWith(windows, StringComparison.OrdinalIgnoreCase))) continue;
            string[] settings;
            try
            {
                settings = Directory.GetFiles(dir, "appsettings*.json");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in settings)
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (var r in KestrelCertificates.Find(text))
                    addFile(Resolve(r.Path, dir), new CertificateUsage { Kind = "service", Target = svc.Name, Detail = $"{Path.GetFileName(file)} · {r.Section}" }, r.Password);
            }
        }
    }

    // ------------------------------------------------------------------
    // Prüfen, was tatsächlich ausgeliefert wird
    // ------------------------------------------------------------------

    private static async Task ProbeAsync(List<CertificateInfo> certs, CancellationToken ct)
    {
        var endpoints = certs.SelectMany(c => c.Usages).Where(u => u.Endpoint is not null).Select(u => u.Endpoint!).Distinct(StringComparer.OrdinalIgnoreCase).Take(60).ToList();
        var results = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(endpoints, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (ep, token) =>
            results[ep] = await ServedThumbprintAsync(ep, token));
        foreach (var u in certs.SelectMany(c => c.Usages))
            if (u.Endpoint is { } ep && results.TryGetValue(ep, out var t))
                u.ServedThumbprint = t;
    }

    /// <summary>Verbindet sich lokal (127.0.0.1) mit SNI = Hostname und liefert den Fingerabdruck des ausgelieferten Zertifikats.</summary>
    private static async Task<string?> ServedThumbprintAsync(string endpoint, CancellationToken ct)
    {
        var i = endpoint.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(endpoint[(i + 1)..], out var port)) return null;
        var host = endpoint[..i];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port, cts.Token);
            string? thumb = null;
            await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
            {
                thumb = cert is null ? null : Convert.ToHexString(cert.GetCertHash());
                return true;
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
            return thumb;
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // win-acme finden und Einstellungen
    // ------------------------------------------------------------------

    private async Task<WinAcmeInfo> WinAcmeAsync(List<string> notes, CancellationToken ct)
    {
        var info = new WinAcmeInfo();
        var task = await TaskAsync(ct);
        var exe = LocateWacs(task?.Exe);
        if (task is not null)
        {
            info.TaskName = task.Name;
            info.TaskState = task.State;
            info.TaskLastRun = task.LastRun;
            info.TaskLastResult = task.LastResult;
            info.TaskNextRun = task.NextRun;
        }
        if (exe is null)
        {
            notes.Add("win-acme (wacs.exe) wurde nicht gefunden – in der Agent-Einstellung „WinAcmePath“ eintragen.");
            return info;
        }
        info.Installed = true;
        info.ExePath = exe;
        try
        {
            info.Version = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0];
        }
        catch (FileNotFoundException) { }

        var dir = Path.GetDirectoryName(exe)!;
        var settings = new[] { "settings.json", "settings_default.json" }.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
        info.SettingsPath = settings ?? Path.Combine(dir, "settings.json");
        string? configRoot = null;
        if (settings is not null)
        {
            try
            {
                var root = JsonNode.Parse(await File.ReadAllTextAsync(settings, ct), documentOptions: JsonRead);
                info.BaseUri = Str(root, "Acme", "DefaultBaseUri");
                info.RenewalDays = Int(root, "ScheduledTask", "RenewalDays");
                info.RenewalMinimumValidDays = Int(root, "ScheduledTask", "RenewalMinimumValidDays");
                info.DefaultValidDays = Int(root, "Order", "DefaultValidDays");
                configRoot = Str(root, "Client", "ConfigurationPath");
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                notes.Add($"win-acme settings.json nicht lesbar: {e.Message}");
            }
        }
        info.RenewalDays ??= 55;
        configRoot = string.IsNullOrWhiteSpace(configRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "win-acme")
            : Environment.ExpandEnvironmentVariables(configRoot);
        var host = Uri.TryCreate(info.BaseUri ?? "", UriKind.Absolute, out var uri) ? uri.Host : null;
        info.ConfigPath = host is not null && Directory.Exists(Path.Combine(configRoot, host)) ? Path.Combine(configRoot, host) : configRoot;
        return info;
    }

    private static IEnumerable<string> RenewalFiles(WinAcmeInfo info)
    {
        if (info.ConfigPath is not { } dir || !Directory.Exists(dir)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(dir, "*.renewal.json", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateDirectories(dir).SelectMany(d => Directory.EnumerateFiles(d, "*.renewal.json", SearchOption.TopDirectoryOnly)))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private string? LocateWacs(string? fromTask)
    {
        var candidates = new List<string>();
        var configured = Environment.ExpandEnvironmentVariables(options.WinAcmePath.Trim());
        if (configured.Length > 0) candidates.Add(Directory.Exists(configured) ? Path.Combine(configured, "wacs.exe") : configured);
        if (fromTask is not null) candidates.Add(fromTask);
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"C:\", @"C:\tools" })
            if (root.Length > 0)
                candidates.AddRange(new[] { "win-acme", "wacs", "win-acme.v2" }.Select(d => Path.Combine(root, d, "wacs.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Die geplante Aufgabe von win-acme (Name, Zustand, letzter/nächster Lauf) über PowerShell – sprachunabhängig.</summary>
    private async Task<WinAcmeTask?> TaskAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _taskCache.At < TimeSpan.FromSeconds(30)) return _taskCache.Task;
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            $t = Get-ScheduledTask | Where-Object { $_.Actions | Where-Object { $_.Execute -like '*wacs*' } } | Select-Object -First 1
            if ($t) {
              $i = $t | Get-ScheduledTaskInfo
              $a = $t.Actions | Where-Object { $_.Execute -like '*wacs*' } | Select-Object -First 1
              $exe = $a.Execute.Trim('"')
              if (-not [IO.Path]::IsPathRooted($exe) -and $a.WorkingDirectory) { $exe = Join-Path $a.WorkingDirectory.Trim('"') $exe }
              [pscustomobject]@{
                Name = $t.TaskName; State = "$($t.State)"; Exe = $exe
                LastRun = $(if ($i.LastRunTime -and $i.LastRunTime.Year -gt 2000) { $i.LastRunTime.ToUniversalTime().ToString('o') } else { $null })
                LastResult = $i.LastTaskResult
                NextRun = $(if ($i.NextRunTime) { $i.NextRunTime.ToUniversalTime().ToString('o') } else { $null })
              } | ConvertTo-Json -Compress
            }
            """;
        WinAcmeTask? task = null;
        try
        {
            var (code, output) = await RunCaptureAsync("powershell.exe",
                new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) },
                TimeSpan.FromSeconds(30), ct);
            var json = output.Trim();
            if (code == 0 && json.StartsWith('{'))
            {
                var node = JsonNode.Parse(json);
                task = new WinAcmeTask(
                    Str(node, "Name") ?? "", Str(node, "State") ?? "", Str(node, "Exe") ?? "",
                    ParseDate(Str(node, "LastRun")), node?["LastResult"] is JsonValue v && v.TryGetValue<long>(out var r) ? r : null, ParseDate(Str(node, "NextRun")));
            }
        }
        catch (Exception e) when (e is Win32Exception or JsonException or InvalidOperationException)
        {
            log.LogDebug(e, "Aufgabe von win-acme nicht lesbar");
        }
        _taskCache = (DateTime.UtcNow, task);
        return task;
    }

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static string? Str(JsonNode? node, params string[] path)
    {
        foreach (var p in path) node = node is JsonObject o ? o.FirstOrDefault(x => x.Key.Equals(p, StringComparison.OrdinalIgnoreCase)).Value : null;
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    private static int? Int(JsonNode? node, params string[] path)
    {
        foreach (var p in path) node = node is JsonObject o ? o.FirstOrDefault(x => x.Key.Equals(p, StringComparison.OrdinalIgnoreCase)).Value : null;
        return node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    }

    /// <summary>Richtlinie in die settings.json von win-acme schreiben (mit Sicherung).</summary>
    public async Task<WinAcmeInfo> SetPolicyAsync(CertificatePolicy policy)
    {
        policy.Normalize();
        var notes = new List<string>();
        var info = await WinAcmeAsync(notes, CancellationToken.None);
        if (!info.Installed || info.SettingsPath is not { } path) throw new InvalidOperationException(notes.FirstOrDefault() ?? "win-acme nicht gefunden.");
        JsonObject root;
        if (File.Exists(path))
        {
            root = JsonNode.Parse(await File.ReadAllTextAsync(path), documentOptions: JsonRead) as JsonObject ?? new JsonObject();
            File.Copy(path, path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".bak", overwrite: true);
        }
        else
        {
            root = new JsonObject();
        }
        var (renewalDays, minimum) = policy.ForWinAcme();
        var task = Section(root, "ScheduledTask");
        task["RenewalDays"] = renewalDays;
        task["RenewalMinimumValidDays"] = minimum;
        Section(root, "Order")["DefaultValidDays"] = policy.RequestValidDays ? policy.ValidDays : null;
        await File.WriteAllTextAsync(path, root.ToJsonString(JsonWrite), new UTF8Encoding(false));
        log.LogInformation("win-acme-Richtlinie gesetzt: RenewalDays={Days}, RenewalMinimumValidDays={Min}, DefaultValidDays={Valid}", renewalDays, minimum, policy.RequestValidDays ? policy.ValidDays : null);
        return await WinAcmeAsync(notes, CancellationToken.None);
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        var key = root.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Key;
        if (key is not null && root[key] is JsonObject existing) return existing;
        var created = new JsonObject();
        root[key ?? name] = created;
        return created;
    }

    // ------------------------------------------------------------------
    // Läufe (wacs.exe / nginx)
    // ------------------------------------------------------------------

    public List<CertificateJob> Jobs() => _jobs.Values.OrderByDescending(j => j.Started).Select(Snapshot).ToList();

    public CertificateJob Job(string id) => _jobs.TryGetValue(id, out var j) ? Snapshot(j) : throw new NotFoundException($"Lauf {id} gibt es nicht.");

    /// <summary>Kopie zum Ausliefern – das Log wächst währenddessen weiter.</summary>
    private static CertificateJob Snapshot(CertificateJob j)
    {
        lock (j.Log)
        {
            return new CertificateJob
            {
                Id = j.Id, Kind = j.Kind, Title = j.Title, State = j.State, Started = j.Started, Finished = j.Finished,
                ExitCode = j.ExitCode, CommandLine = j.CommandLine, Log = j.Log.ToList(),
            };
        }
    }

    public async Task<CertificateJob> RenewAsync(string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$")) throw new ArgumentException("Ungültige Erneuerungs-ID.");
        return Start("renew", $"Erneuern: {await RenewalName(id)}", await WacsAsync(), WinAcmeArguments.Renew(id));
    }

    public async Task<CertificateJob> RenewAllAsync(bool force) =>
        Start("renew", force ? "Alle Zertifikate erneuern" : "Fällige Zertifikate erneuern", await WacsAsync(),
            force ? new List<string> { "--renew", "--force", "--nocache" } : new List<string> { "--renew" });

    public async Task<CertificateJob> CancelAsync(string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$")) throw new ArgumentException("Ungültige Erneuerungs-ID.");
        return Start("cancel", $"Erneuerung entfernen: {await RenewalName(id)}", await WacsAsync(), WinAcmeArguments.Cancel(id));
    }

    public async Task<CertificateJob> CreateAsync(AcmeOrderRequest request)
    {
        var hook = Path.Combine(AppContext.BaseDirectory, "Cert-Hook.ps1");
        var args = WinAcmeArguments.Create(request, File.Exists(hook) ? hook : null, request.ReloadNginx ? NginxExe() : null);
        return Start("create", "Neues Zertifikat: " + string.Join(", ", request.Hosts), await WacsAsync(), args);
    }

    public CertificateJob ReloadNginx()
    {
        var exe = NginxExe() ?? throw new InvalidOperationException("Auf diesem Server wurde kein nginx gefunden.");
        var prefix = Path.GetDirectoryName(exe)! + Path.DirectorySeparatorChar;
        var job = NewJob("nginx", "nginx neu laden", $"{exe} -t && {exe} -s reload");
        _ = Task.Run(async () =>
        {
            var code = await RunJobAsync(job, exe, new[] { "-p", prefix, "-t" });
            if (code == 0) code = await RunJobAsync(job, exe, new[] { "-p", prefix, "-s", "reload" });
            Finish(job, code);
        });
        return Snapshot(job);
    }

    private async Task<string> RenewalName(string id)
    {
        var info = await WinAcmeAsync(new List<string>(), CancellationToken.None);
        var file = RenewalFiles(info).FirstOrDefault(f => Path.GetFileName(f).StartsWith(id + ".", StringComparison.OrdinalIgnoreCase))
                   ?? throw new NotFoundException($"Erneuerung {id} gibt es nicht.");
        try
        {
            var r = WinAcmeRenewalParser.Parse(await File.ReadAllTextAsync(file), file);
            return r.Hosts.Count > 0 ? string.Join(", ", r.Hosts) : r.Name;
        }
        catch (JsonException)
        {
            return id;
        }
    }

    private async Task<string> WacsAsync()
    {
        var info = await WinAcmeAsync(new List<string>(), CancellationToken.None);
        return info.ExePath ?? throw new InvalidOperationException("win-acme (wacs.exe) wurde nicht gefunden – in der Agent-Einstellung „WinAcmePath“ eintragen.");
    }

    private CertificateJob Start(string kind, string title, string exe, List<string> args)
    {
        if (_jobs.Values.Any(j => !j.IsFinished && j.Kind != "nginx"))
            throw new InvalidOperationException("win-acme läuft bereits – bitte warten, bis der aktuelle Lauf fertig ist.");
        var job = NewJob(kind, title, WinAcmeArguments.Display(exe, args));
        _ = Task.Run(async () => Finish(job, await RunJobAsync(job, exe, args)));
        return Snapshot(job);
    }

    private CertificateJob NewJob(string kind, string title, string commandLine)
    {
        foreach (var old in _jobs.Values.Where(j => j.IsFinished).OrderByDescending(j => j.Started).Skip(20).ToList())
            _jobs.TryRemove(old.Id, out _);
        var job = new CertificateJob
        {
            Id = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6],
            Kind = kind, Title = title, Started = DateTime.UtcNow, CommandLine = commandLine,
        };
        _jobs[job.Id] = job;
        log.LogInformation("Zertifikat: {Title} – {Command}", title, commandLine);
        return job;
    }

    private void Finish(CertificateJob job, int code)
    {
        lock (job.Log) job.Log.Add(code == 0 ? "Fertig." : $"Beendet mit Fehlercode {code}.");
        job.ExitCode = code;
        job.State = code == 0 ? "succeeded" : "failed";
        job.Finished = DateTime.UtcNow;
        _taskCache = default;
        log.LogInformation("Zertifikat: {Title} beendet mit {Code}", job.Title, code);
    }

    private static async Task<int> RunJobAsync(CertificateJob job, string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Prozess konnte nicht gestartet werden.");
            p.StandardInput.Close();
            void Add(string? line)
            {
                if (line is null) return;
                line = Ansi().Replace(line, "").TrimEnd();
                if (line.Length == 0) return;
                lock (job.Log)
                {
                    if (job.Log.Count < 5000) job.Log.Add(line);
                }
            }
            p.OutputDataReceived += (_, e) => Add(e.Data);
            p.ErrorDataReceived += (_, e) => Add(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                p.Kill(entireProcessTree: true);
                Add("Abgebrochen: Zeitüberschreitung (15 Minuten).");
                return -2;
            }
            p.WaitForExit(2000);
            return p.ExitCode;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            lock (job.Log) job.Log.Add("Fehler: " + e.Message);
            return -1;
        }
    }

    private static async Task<(int Code, string Output)> RunCaptureAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(exe + " konnte nicht gestartet werden.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var output = p.StandardOutput.ReadToEndAsync(cts.Token);
        _ = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return (p.ExitCode, await output);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            return (-1, "");
        }
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();
}
