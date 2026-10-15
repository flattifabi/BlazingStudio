using System.Text.RegularExpressions;

namespace BlazorStudio.Deploy;

/// <summary>
/// Dateien und Ordner einer Anwendung, die von Version zu Version mitwandern (z. B. eine SQLite-Datenbank,
/// App_Data, hochgeladene Dateien). Beim Umschalten nimmt der Agent die App kurz offline (app_offline.htm),
/// verschiebt die Einträge aus der laufenden in die neue Version und schaltet dann um.
/// </summary>
public static partial class PersistentPaths
{
    public const int MaxEntries = 50;

    /// <summary>Begleitdateien von SQLite, die mit einer Datenbankdatei mitwandern müssen.</summary>
    public static readonly string[] SqliteCompanions = { "-wal", "-shm", "-journal" };

    [GeneratedRegex(@"^[A-Za-z0-9 _.\-+@()\[\]]+$")]
    private static partial Regex SegmentPattern();

    /// <summary>
    /// Relativer Pfad mit „/“ („App_Data“, „data/app.db“) oder null, wenn ungültig: kein absoluter Pfad, kein „..“,
    /// nicht die web.config (die wird über die Konfiguration verwaltet).
    /// </summary>
    public static string? Normalize(string? path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/').Trim('/');
        if (p.Length == 0 || p.Length > 240 || p.Contains(':')) return null;
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var s in segments)
        {
            var t = s.Trim();
            if (t.Length == 0 || t == "." || t == ".." || !SegmentPattern().IsMatch(t) || t.EndsWith('.')) return null;
        }
        var result = string.Join('/', segments.Select(s => s.Trim()));
        if (result.Equals(SiteConfig.WebConfig, StringComparison.OrdinalIgnoreCase) || result.Equals("app_offline.htm", StringComparison.OrdinalIgnoreCase)) return null;
        return result;
    }

    /// <summary>Liste aus Text (Zeilen, Komma oder Semikolon). Ungültige Einträge landen in <paramref name="invalid"/>.</summary>
    public static List<string> Parse(string? text, out List<string> invalid)
    {
        invalid = new();
        var list = new List<string>();
        foreach (var raw in (text ?? "").Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith('#')) continue;
            var n = Normalize(raw);
            if (n is null) invalid.Add(raw);
            else if (!list.Contains(n, StringComparer.OrdinalIgnoreCase)) list.Add(n);
        }
        if (list.Count > MaxEntries) list = list.Take(MaxEntries).ToList();
        return list;
    }

    /// <summary>Prüft eine Liste aus der API. Wirft bei ungültigen Einträgen.</summary>
    public static List<string> Validate(IEnumerable<string>? paths)
    {
        var list = Parse(string.Join('\n', paths ?? Array.Empty<string>()), out var invalid);
        if (invalid.Count > 0) throw new ArgumentException("Ungültige Pfade (nur relative Pfade innerhalb der Anwendung, ohne „..“, nicht web.config): " + string.Join(", ", invalid));
        return list;
    }

    /// <summary>Plattformgerechter Pfad unterhalb von <paramref name="root"/>.</summary>
    public static string Combine(string root, string normalized) => Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>GET/PUT /api/sites/{name}/persist.</summary>
public sealed class PersistentSettings
{
    public List<string> Paths { get; set; } = new();
}

/// <summary>
/// Verschiebt persistente Einträge von einem Versionsordner in einen anderen (gleiches Laufwerk → sofort, auch bei
/// großen Datenbanken). Gesperrte Dateien werden bis zum Timeout erneut versucht. Undo legt alles zurück.
/// </summary>
public sealed class PersistentMove
{
    private readonly List<(string From, string To, bool IsDirectory)> _moved = new();

    public IReadOnlyList<(string From, string To, bool IsDirectory)> Moved => _moved;

    public static async Task<PersistentMove> RunAsync(string fromDir, string toDir, IReadOnlyList<string> paths, Action<string, string> log,
        TimeSpan unlockTimeout, CancellationToken ct = default)
    {
        var move = new PersistentMove();
        try
        {
            foreach (var p in paths)
            {
                var src = PersistentPaths.Combine(fromDir, p);
                var dst = PersistentPaths.Combine(toDir, p);
                if (Directory.Exists(src))
                {
                    await RetryAsync(p, unlockTimeout, ct, () =>
                    {
                        if (Directory.Exists(dst)) Directory.Delete(dst, true);
                        else if (File.Exists(dst)) File.Delete(dst);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        Directory.Move(src, dst);
                    });
                    move._moved.Add((src, dst, true));
                    log($"Übernommen: {p}/", "info");
                }
                else if (File.Exists(src))
                {
                    foreach (var suffix in PersistentPaths.SqliteCompanions.Prepend(""))
                    {
                        var s = src + suffix;
                        var d = dst + suffix;
                        if (File.Exists(s))
                        {
                            await RetryAsync(p + suffix, unlockTimeout, ct, () =>
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                                File.Move(s, d, overwrite: true);
                            });
                            move._moved.Add((s, d, false));
                        }
                        else if (suffix.Length > 0 && File.Exists(d))
                        {
                            // Begleitdatei aus dem Paket gehört nicht zur übernommenen Datenbank
                            File.Delete(d);
                        }
                    }
                    log($"Übernommen: {p}", "info");
                }
                else
                {
                    log($"{p}: in der laufenden Version nicht vorhanden – bleibt wie im Paket", "info");
                }
            }
        }
        catch
        {
            move.Undo(log);
            throw;
        }
        return move;
    }

    /// <summary>Alles zurück an den alten Ort (z. B. wenn das Umschalten scheitert).</summary>
    public void Undo(Action<string, string> log)
    {
        for (var i = _moved.Count - 1; i >= 0; i--)
        {
            var (from, to, isDir) = _moved[i];
            try
            {
                if (isDir)
                {
                    if (!Directory.Exists(to)) continue;
                    if (Directory.Exists(from)) Directory.Delete(from, true);
                    Directory.Move(to, from);
                }
                else if (File.Exists(to))
                {
                    File.Move(to, from, overwrite: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"Konnte {to} nicht zurücklegen: {e.Message}", "error");
            }
        }
        _moved.Clear();
    }

    private static async Task RetryAsync(string what, TimeSpan timeout, CancellationToken ct, Action action)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new IOException($"{what} ist noch in Benutzung und konnte nicht übernommen werden: {e.Message}", e);
                await Task.Delay(500, ct);
            }
        }
    }
}
