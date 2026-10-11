using System.Text;
using System.Text.RegularExpressions;

namespace BlazorStudio.Core.Search;

public sealed record SearchOptions(string Query, bool MatchCase = false, bool WholeWord = false, bool UseRegex = false, string? FileMask = null);

/// <summary>Ein Treffer: Zeile/Spalte 1-basiert, Offset/Länge im Dateitext.</summary>
public sealed record SearchHit(string File, int Line, int Column, int Offset, int Length, string LineText);

public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int FilesSearched, bool Truncated, string? Error);

/// <summary>
/// „In Dateien suchen/ersetzen“ wie Strg+Shift+F in Rider/VS: Groß/Klein, ganzes Wort, Regex, Dateifilter
/// (z. B. "*.cs;*.razor;!*.min.js"). Überspringt bin/obj/.git/node_modules und Binärdateien.
/// </summary>
public static class FileSearch
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages", "TestResults" };
    private static readonly HashSet<string> BinaryExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".bmp", ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".zip", ".nupkg", ".snupkg", ".db", ".sqlite", ".mp4", ".mp3", ".pdf", ".7z", ".gz", ".cache",
    };

    public const long MaxFileSize = 4 * 1024 * 1024;

    /// <summary>Regex für die Suche (auch für normale Texte, dann escaped).</summary>
    public static Regex? BuildRegex(SearchOptions o, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(o.Query)) return null;
        var pattern = o.UseRegex ? o.Query : Regex.Escape(o.Query);
        if (o.WholeWord) pattern = @"(?<![\w])(?:" + pattern + @")(?![\w])";
        try
        {
            return new Regex(pattern, (o.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>Alle durchsuchbaren Dateien unter den Wurzeln (Maske beachtet).</summary>
    public static IEnumerable<string> EnumerateFiles(IEnumerable<string> roots, string? mask)
    {
        var (include, exclude) = ParseMask(mask);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (var d in dirs.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                    if (!SkipDirs.Contains(Path.GetFileName(d))) stack.Push(d);
                foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (BinaryExt.Contains(Path.GetExtension(f))) continue;
                    var name = Path.GetFileName(f);
                    if (include.Count > 0 && !include.Any(r => r.IsMatch(name))) continue;
                    if (exclude.Any(r => r.IsMatch(name))) continue;
                    if (seen.Add(f)) yield return f;
                }
            }
        }
    }

    private static (List<Regex> Include, List<Regex> Exclude) ParseMask(string? mask)
    {
        var inc = new List<Regex>();
        var exc = new List<Regex>();
        foreach (var part in (mask ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var neg = part.StartsWith('!');
            var glob = neg ? part[1..] : part;
            if (glob.Length == 0) continue;
            var rx = new Regex("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            (neg ? exc : inc).Add(rx);
        }
        return (inc, exc);
    }

    /// <summary>Durchsucht Dateien. <paramref name="overrides"/>: ungespeicherte Editor-Inhalte (Pfad → Text).</summary>
    public static SearchResult Search(IEnumerable<string> roots, SearchOptions o, IReadOnlyDictionary<string, string>? overrides = null, int maxHits = 5000, CancellationToken ct = default)
    {
        var rx = BuildRegex(o, out var error);
        if (rx is null) return new SearchResult(Array.Empty<SearchHit>(), 0, false, error);
        var hits = new List<SearchHit>();
        var count = 0;
        foreach (var file in EnumerateFiles(roots, o.FileMask))
        {
            ct.ThrowIfCancellationRequested();
            var text = overrides is not null && overrides.TryGetValue(file, out var t) ? t : ReadText(file);
            if (text is null) continue;
            count++;
            if (SearchText(file, text, rx, hits, maxHits)) return new SearchResult(hits, count, true, null);
        }
        return new SearchResult(hits, count, false, null);
    }

    /// <summary>Treffer in einem Text sammeln; true, wenn das Limit erreicht ist.</summary>
    public static bool SearchText(string file, string text, Regex rx, List<SearchHit> hits, int maxHits)
    {
        int[]? lineStarts = null;
        foreach (Match m in rx.Matches(text))
        {
            if (m.Length == 0) continue;
            lineStarts ??= LineStarts(text);
            var line = UpperBound(lineStarts, m.Index) - 1;
            var start = lineStarts[line];
            var end = line + 1 < lineStarts.Length ? lineStarts[line + 1] : text.Length;
            var lineText = text[start..end].TrimEnd('\r', '\n');
            hits.Add(new SearchHit(file, line + 1, m.Index - start + 1, m.Index, Math.Min(m.Length, Math.Max(1, lineText.Length - (m.Index - start))), lineText.Length > 400 ? lineText[..400] : lineText));
            if (hits.Count >= maxHits) return true;
        }
        return false;
    }

    /// <summary>Ersetzt alle Treffer im Text. Bei Regex sind $1, ${name} usw. erlaubt.</summary>
    public static (string Text, int Count) ReplaceText(string text, SearchOptions o, string replacement)
    {
        var rx = BuildRegex(o, out _);
        if (rx is null) return (text, 0);
        var count = 0;
        var result = rx.Replace(text, m =>
        {
            count++;
            return o.UseRegex ? m.Result(replacement) : replacement;
        });
        return (result, count);
    }

    public static string? ReadText(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxFileSize) return null;
            var bytes = File.ReadAllBytes(file);
            // Binärdatei? (NUL-Bytes am Anfang)
            for (var i = 0; i < Math.Min(bytes.Length, 8000); i++)
                if (bytes[i] == 0) return null;
            return new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int[] LineStarts(string text)
    {
        var list = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') list.Add(i + 1);
        return list.ToArray();
    }

    private static int UpperBound(int[] a, int v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (a[mid] <= v) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
