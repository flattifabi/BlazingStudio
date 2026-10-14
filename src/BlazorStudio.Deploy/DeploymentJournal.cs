using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorStudio.Deploy;

/// <summary>Verlauf der Deployments (JSON-Datei), threadsicher. Hält die letzten <see cref="MaxEntries"/> Einträge.</summary>
public sealed class DeploymentJournal
{
    public const int MaxEntries = 300;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file;
    private readonly object _gate = new();
    private readonly List<DeploymentRecord> _records;

    public DeploymentJournal(string file)
    {
        _file = file;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        _records = Load();
        // Nach einem Neustart des Dienstes können unterbrochene Deployments nicht weiterlaufen
        foreach (var r in _records.Where(r => !r.IsFinished))
        {
            r.State = DeploymentState.Failed;
            r.Error ??= "Agent wurde während des Deployments beendet.";
            r.FinishedAt ??= DateTime.UtcNow;
        }
    }

    private List<DeploymentRecord> Load()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<List<DeploymentRecord>>(File.ReadAllText(_file), Json) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Add(DeploymentRecord record)
    {
        lock (_gate)
        {
            _records.Insert(0, record);
            if (_records.Count > MaxEntries) _records.RemoveRange(MaxEntries, _records.Count - MaxEntries);
            SaveLocked();
        }
    }

    /// <summary>Änderung an einem Eintrag vornehmen und speichern.</summary>
    public void Update(DeploymentRecord record, Action<DeploymentRecord> change)
    {
        lock (_gate)
        {
            change(record);
            SaveLocked();
        }
    }

    public void Log(DeploymentRecord record, string text, string level = "info") =>
        Update(record, r => r.Log.Add(new DeploymentLogLine { At = DateTime.UtcNow, Level = level, Text = text }));

    /// <summary>Kopie (für die Ausgabe über die API, während das Deployment weiterläuft).</summary>
    public DeploymentRecord? Get(string id)
    {
        lock (_gate)
        {
            var r = _records.FirstOrDefault(x => x.Id == id);
            return r is null ? null : Clone(r);
        }
    }

    public List<DeploymentRecord> List(string? site = null, int take = 50, bool withLog = false)
    {
        lock (_gate)
        {
            return _records
                .Where(r => site is null || string.Equals(r.Site, site, StringComparison.OrdinalIgnoreCase))
                .Take(Math.Clamp(take, 1, MaxEntries))
                .Select(r =>
                {
                    var c = Clone(r);
                    if (!withLog) c.Log = new();
                    return c;
                })
                .ToList();
        }
    }

    public int Running
    {
        get { lock (_gate) return _records.Count(r => !r.IsFinished); }
    }

    private static DeploymentRecord Clone(DeploymentRecord r) =>
        JsonSerializer.Deserialize<DeploymentRecord>(JsonSerializer.Serialize(r, Json), Json)!;

    private void SaveLocked()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_records, Json));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Verlauf ist Komfort – Deployment läuft trotzdem weiter
        }
    }
}
