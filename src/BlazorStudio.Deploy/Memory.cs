namespace BlazorStudio.Deploy;

/// <summary>Ein .NET-Prozess auf dem Server, der sich analysieren lässt (GET /api/memory/processes).</summary>
public sealed class MemoryProcess
{
    public int ProcessId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>App-Pool, Dienst oder Programmname.</summary>
    public string Label { get; set; } = "";

    public long WorkingSetBytes { get; set; }

    public bool Is32Bit { get; set; }
}

/// <summary>POST /api/memory/snapshots.</summary>
public sealed class MemorySnapshotRequest
{
    public int ProcessId { get; set; }

    public string? Label { get; set; }
}

/// <summary>Ein Speicher-Snapshot eines Prozesses mit Zusammenfassung.</summary>
public sealed class MemorySnapshotInfo
{
    public string Id { get; set; } = "";

    public int ProcessId { get; set; }

    public string ProcessName { get; set; } = "";

    public string Label { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>running, ready, failed.</summary>
    public string State { get; set; } = "running";

    public string? Step { get; set; }

    public string? Error { get; set; }

    public string? Runtime { get; set; }

    /// <summary>Wie lange der Prozess für den Snapshot angehalten war.</summary>
    public long PauseMs { get; set; }

    public long AnalysisMs { get; set; }

    /// <summary>Details (Objekte, Felder, Wurzeln) sind abrufbar, solange der Snapshot geöffnet ist.</summary>
    public bool DetailsAvailable { get; set; }

    public long WorkingSetBytes { get; set; }

    public long HeapBytes { get; set; }

    public long ObjectCount { get; set; }

    public int TypeCount { get; set; }

    public long Gen0Bytes { get; set; }

    public long Gen1Bytes { get; set; }

    public long Gen2Bytes { get; set; }

    public long LargeBytes { get; set; }

    public long PinnedBytes { get; set; }

    /// <summary>Freier Platz im Heap (Fragmentierung).</summary>
    public long FreeBytes { get; set; }

    public bool IsReady => State == "ready";
}

/// <summary>Speicher je Typ.</summary>
public sealed class MemoryTypeStat
{
    public string Type { get; set; } = "";

    public long Count { get; set; }

    public long Bytes { get; set; }

    /// <summary>Größtes einzelnes Objekt dieses Typs.</summary>
    public long MaxBytes { get; set; }
}

/// <summary>Ein Objekt in einer Liste.</summary>
public sealed class MemoryObjectRow
{
    public ulong Address { get; set; }

    public string Type { get; set; } = "";

    public long Size { get; set; }

    public string Generation { get; set; } = "";

    /// <summary>Kurzinhalt (Text eines Strings, Länge eines Arrays …).</summary>
    public string? Preview { get; set; }
}

/// <summary>Ein Feld bzw. Array-Element eines Objekts.</summary>
public sealed class MemoryField
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = "";

    /// <summary>Lesbarer Wert (Zahl, Text, „null“ …).</summary>
    public string? Value { get; set; }

    /// <summary>Adresse des referenzierten Objekts (zum Weiterklicken), sonst null.</summary>
    public ulong? Reference { get; set; }
}

public sealed class MemoryObjectDetail
{
    public ulong Address { get; set; }

    public string Type { get; set; } = "";

    public long Size { get; set; }

    public string Generation { get; set; } = "";

    /// <summary>Text bei Strings.</summary>
    public string? Text { get; set; }

    /// <summary>Länge bei Arrays.</summary>
    public int? Length { get; set; }

    public List<MemoryField> Fields { get; set; } = new();

    /// <summary>Ab Element <see cref="ElementsFrom"/> (höchstens 200 je Abruf).</summary>
    public List<MemoryField> Elements { get; set; } = new();

    public int ElementsFrom { get; set; }

    /// <summary>Größe aller Objekte, die von diesem aus erreichbar sind (inklusive; was es mindestens am Leben halten kann).</summary>
    public long? ReachableBytes { get; set; }

    public long? ReachableObjects { get; set; }

    /// <summary>Berechnung der erreichbaren Größe vorzeitig beendet (sehr großer Graph).</summary>
    public bool ReachableTruncated { get; set; }
}

/// <summary>Ein Pfad von einer GC-Wurzel zum Objekt: „wer hält das fest?“.</summary>
public sealed class MemoryRootPath
{
    /// <summary>Stack, StrongHandle, StaticVar …</summary>
    public string RootKind { get; set; } = "";

    public List<MemoryPathStep> Steps { get; set; } = new();
}

public sealed class MemoryPathStep
{
    public ulong Address { get; set; }

    public string Type { get; set; } = "";

    /// <summary>Feld bzw. Index, über das der nächste Schritt erreicht wird.</summary>
    public string? Via { get; set; }
}

public sealed class MemoryRootResult
{
    public List<MemoryRootPath> Paths { get; set; } = new();

    /// <summary>Suche abgebrochen (zu viele Objekte/Zeit) – Pfade evtl. unvollständig.</summary>
    public bool Truncated { get; set; }

    public long Visited { get; set; }
}

/// <summary>Vergleich zweier Snapshots je Typ (wächst? schrumpft?).</summary>
public sealed class MemoryTypeDelta
{
    public string Type { get; set; } = "";

    public long CountBefore { get; set; }

    public long CountAfter { get; set; }

    public long BytesBefore { get; set; }

    public long BytesAfter { get; set; }

    public long CountDelta => CountAfter - CountBefore;

    public long BytesDelta => BytesAfter - BytesBefore;

    /// <summary>Typlisten zweier Snapshots vergleichen, sortiert nach Zuwachs in Bytes.</summary>
    /// <summary>Kurzname für Pfade/Brotkrumen: „System.Collections.Generic.List&lt;MyApp.Order&gt;“ → „List&lt;Order&gt;“.</summary>
    public static string ShortType(string type)
    {
        var sb = new System.Text.StringBuilder(type.Length);
        var start = 0;
        for (var i = 0; i <= type.Length; i++)
        {
            if (i < type.Length && type[i] is not ('<' or '>' or ',' or '[' or ']' or ' ')) continue;
            var part = type[start..i];
            sb.Append(part[(Math.Max(part.LastIndexOf('+'), part.LastIndexOf('.')) + 1)..]);
            if (i < type.Length) sb.Append(type[i]);
            start = i + 1;
        }
        return sb.ToString();
    }

    public static List<MemoryTypeDelta> Compare(IEnumerable<MemoryTypeStat> before, IEnumerable<MemoryTypeStat> after)
    {
        var map = new Dictionary<string, MemoryTypeDelta>(StringComparer.Ordinal);
        foreach (var b in before) map[b.Type] = new MemoryTypeDelta { Type = b.Type, CountBefore = b.Count, BytesBefore = b.Bytes };
        foreach (var a in after)
        {
            if (!map.TryGetValue(a.Type, out var d)) map[a.Type] = d = new MemoryTypeDelta { Type = a.Type };
            d.CountAfter = a.Count;
            d.BytesAfter = a.Bytes;
        }
        return map.Values.Where(d => d.CountDelta != 0 || d.BytesDelta != 0)
            .OrderByDescending(d => d.BytesDelta).ThenByDescending(d => d.CountDelta).ToList();
    }
}
