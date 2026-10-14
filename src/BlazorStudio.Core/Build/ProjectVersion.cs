using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BlazorStudio.Core.Build;

public enum VersionPart { Major, Minor, Patch }

/// <summary>
/// Liest und setzt die Version eines Projekts (&lt;Version&gt; bzw. &lt;VersionPrefix&gt; in der .csproj),
/// ohne die übrige Formatierung der Datei anzufassen.
/// </summary>
public static partial class ProjectVersion
{
    [GeneratedRegex(@"<(Version|VersionPrefix)>\s*([^<]*?)\s*</\1>")]
    private static partial Regex VersionElement();

    [GeneratedRegex(@"^\d+(\.\d+){0,3}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Valid();

    /// <summary>Gültige Versionsangabe für MSBuild/NuGet (z. B. 1.2.3, 1.2.3-beta.1, 2.0).</summary>
    public static bool IsValid(string? version) => version is not null && Valid().IsMatch(version.Trim());

    /// <summary>Aktuelle Version oder null, wenn die Projektdatei keine angibt (MSBuild nimmt dann 1.0.0).</summary>
    public static string? Read(string csprojText)
    {
        var m = VersionElement().Match(csprojText);
        return m.Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : null;
    }

    /// <summary>
    /// Setzt die Version: ändert ein vorhandenes &lt;Version&gt;/&lt;VersionPrefix&gt;, sonst kommt &lt;Version&gt; in die
    /// erste PropertyGroup (bzw. eine neue). Der Rest der Datei bleibt unverändert.
    /// </summary>
    public static string Write(string csprojText, string version)
    {
        version = version.Trim();
        if (!IsValid(version)) throw new ArgumentException($"Ungültige Version „{version}“ – erwartet z. B. 1.2.3 oder 1.2.3-beta.1.", nameof(version));
        var m = VersionElement().Match(csprojText);
        if (m.Success)
        {
            var g = m.Groups[2];
            return csprojText[..g.Index] + version + csprojText[(g.Index + g.Length)..];
        }

        var group = csprojText.IndexOf("<PropertyGroup>", StringComparison.Ordinal);
        if (group >= 0)
        {
            // Einrückung der nächsten Zeile übernehmen
            var after = group + "<PropertyGroup>".Length;
            var nl = csprojText.IndexOf('\n', after);
            var indent = "    ";
            if (nl >= 0)
            {
                var i = nl + 1;
                var start = i;
                while (i < csprojText.Length && csprojText[i] is ' ' or '\t') i++;
                if (i > start && i < csprojText.Length && csprojText[i] == '<') indent = csprojText[start..i];
            }
            var eol = csprojText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            return csprojText[..after] + eol + indent + "<Version>" + version + "</Version>" + csprojText[after..];
        }

        var project = Regex.Match(csprojText, @"<Project\b[^>]*>");
        if (!project.Success) throw new InvalidDataException("Keine gültige Projektdatei (<Project> fehlt).");
        var end = project.Index + project.Length;
        var nlStyle = csprojText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return csprojText[..end] + nlStyle + nlStyle + "  <PropertyGroup>" + nlStyle + "    <Version>" + version + "</Version>" + nlStyle + "  </PropertyGroup>" + csprojText[end..];
    }

    /// <summary>1.2.3 → 1.2.4 (Patch), 1.3.0 (Minor), 2.0.0 (Major). Vorabkennung wird entfernt.</summary>
    public static string Bump(string? version, VersionPart part)
    {
        var core = (version ?? "1.0.0").Split('-', '+')[0];
        var nums = core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
        while (nums.Count < 3) nums.Add(0);
        switch (part)
        {
            case VersionPart.Major: nums[0]++; nums[1] = 0; nums[2] = 0; break;
            case VersionPart.Minor: nums[1]++; nums[2] = 0; break;
            default: nums[2]++; break;
        }
        return string.Join('.', nums.Take(3));
    }

    /// <summary>Prüft, ob der Text noch gültiges XML ist (Sicherheitsnetz vor dem Speichern).</summary>
    public static bool IsWellFormed(string csprojText)
    {
        try
        {
            XDocument.Parse(csprojText);
            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }
}
