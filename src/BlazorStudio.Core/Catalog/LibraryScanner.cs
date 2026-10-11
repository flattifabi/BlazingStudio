using System.Reflection;
using System.Runtime.InteropServices;

namespace BlazorStudio.Core.Catalog;

/// <summary>Eine Komponenten-Bibliothek (NuGet-Paket), z. B. MudBlazor.</summary>
public sealed record ComponentLibrary(string PackageId, string Version, IReadOnlyList<string> Namespaces, IReadOnlyList<ComponentDefinition> Components);

/// <summary>
/// Findet Razor-Komponenten in Paket-DLLs, ohne sie auszuführen (MetadataLoadContext):
/// alle öffentlichen, nicht abstrakten Typen, die IComponent implementieren, samt [Parameter]-Properties.
/// </summary>
public static class LibraryScanner
{
    private static readonly Dictionary<string, (DateTime Stamp, List<ComponentDefinition> Defs)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pakete, deren Komponenten schon fest eingebaut sind (Forms, Routing …).</summary>
    private static readonly HashSet<string> SkipPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.Web", "Microsoft.AspNetCore.Components.Forms",
        "Microsoft.AspNetCore.Components.WebAssembly", "Microsoft.AspNetCore.Components.Authorization", "Microsoft.AspNetCore.Components.WebView",
        "Microsoft.AspNetCore.Components.WebAssembly.Server", "Microsoft.AspNetCore.Components.WebAssembly.DevServer",
    };

    /// <param name="packages">(Paket-Id, Version, DLL-Pfade) der aufgelösten Pakete.</param>
    public static IReadOnlyList<ComponentLibrary> Scan(IEnumerable<(string Id, string Version, IReadOnlyList<string> Dlls)> packages)
    {
        var list = packages.Where(p => !SkipPackages.Contains(p.Id) && p.Dlls.Count > 0).ToList();
        var allDlls = list.SelectMany(p => p.Dlls).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (allDlls.Count == 0) return Array.Empty<ComponentLibrary>();

        var resolverPaths = new List<string>(allDlls);
        resolverPaths.AddRange(FrameworkAssemblies());
        // Pro Name nur eine Datei, sonst beschwert sich der Resolver
        var unique = resolverPaths.GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        var result = new List<ComponentLibrary>();
        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(unique), "System.Runtime");
        foreach (var (id, version, dlls) in list)
        {
            var defs = new List<ComponentDefinition>();
            foreach (var dll in dlls.Where(File.Exists))
            {
                var stamp = File.GetLastWriteTimeUtc(dll);
                if (Cache.TryGetValue(dll, out var c) && c.Stamp == stamp)
                {
                    defs.AddRange(c.Defs);
                    continue;
                }
                var found = new List<ComponentDefinition>();
                try
                {
                    var asm = mlc.LoadFromAssemblyPath(dll);
                    var docs = XmlDocs.Load(Path.ChangeExtension(dll, ".xml"));
                    foreach (var t in SafeTypes(asm))
                    {
                        var def = TryCreate(t, id, docs);
                        if (def is not null) found.Add(def);
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException) { }
                Cache[dll] = (stamp, found);
                defs.AddRange(found);
            }
            if (defs.Count == 0) continue;
            var namespaces = defs.Select(d => d.Namespace).Where(n => n is not null).Distinct().OrderBy(n => n!.Length).Select(n => n!).ToList();
            result.Add(new ComponentLibrary(id, version, namespaces, defs.OrderBy(d => d.Category).ThenBy(d => d.TagName).ToList()));
        }
        return result;
    }

    private static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try { return asm.GetExportedTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null)!; }
        catch (Exception e) when (e is FileNotFoundException or TypeLoadException) { return Array.Empty<Type>(); }
    }

    private static ComponentDefinition? TryCreate(Type t, string packageId, XmlDocs? docs = null)
    {
        try
        {
            if (!t.IsClass || t.IsAbstract || t.IsNested || !t.IsPublic) return null;
            if (t.Name.StartsWith('_') || t.Name.StartsWith("Base", StringComparison.Ordinal) || t.Name.Contains("Base`", StringComparison.Ordinal) || t.Name.EndsWith("Base", StringComparison.Ordinal)) return null;
            if (t.Name.EndsWith("Provider", StringComparison.Ordinal) || t.Name.Contains("Internal", StringComparison.Ordinal)) return null;
            if (!t.GetInterfaces().Any(i => i.FullName == "Microsoft.AspNetCore.Components.IComponent")) return null;

            var name = t.Name.Contains('`') ? t.Name[..t.Name.IndexOf('`')] : t.Name;
            var generic = t.IsGenericTypeDefinition ? t.GetGenericArguments().Select(a => a.Name).ToList() : new List<string>();

            var parameters = new List<ParameterDefinition>();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attrs = p.CustomAttributes.Select(a => a.AttributeType.FullName).ToList();
                if (!attrs.Contains("Microsoft.AspNetCore.Components.ParameterAttribute")) continue;
                if (parameters.Any(x => x.Name == p.Name)) continue;
                var typeName = FriendlyName(p.PropertyType);
                var editor = ParameterDefinition.EditorFor(typeName);
                IReadOnlyList<string>? options = null;
                string? prefix = null;
                var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? (p.PropertyType.IsGenericType && p.PropertyType.Name.StartsWith("Nullable", StringComparison.Ordinal) ? p.PropertyType.GetGenericArguments()[0] : p.PropertyType);
                if (pt.IsEnum)
                {
                    editor = ValueEditor.Enum;
                    options = pt.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name).ToList();
                    prefix = pt.Name + ".";
                }
                parameters.Add(new ParameterDefinition(p.Name, typeName, editor, options,
                    attrs.Contains("Microsoft.AspNetCore.Components.EditorRequiredAttribute"), docs?.Property(p.DeclaringType ?? t, p.Name), false, prefix));
            }
            if (parameters.Count == 0 && !t.GetInterfaces().Any()) return null;
            parameters = parameters.Select(p =>
                parameters.Any(o => o.Name == p.Name + "Changed" && o.Editor == ValueEditor.Callback) ? p with { IsBindable = true } : p).ToList();

            var acceptsChildren = parameters.Any(p => p.Name == "ChildContent");
            var typeArgs = string.Concat(generic.Select(g => $" {g}=\"string\""));
            var label = Humanize(name, packageId);
            var template = acceptsChildren
                ? $"<{name}{typeArgs}>{(TextLike(name) ? label : "")}</{name}>"
                : $"<{name}{typeArgs} />";

            return new ComponentDefinition
            {
                Key = "lib:" + packageId + ":" + t.FullName,
                TagName = name,
                Label = label,
                Category = CategoryOf(name),
                Icon = IconOf(name),
                Template = template,
                Source = ComponentSource.Library,
                Parameters = parameters.OrderBy(p => p.Editor == ValueEditor.Callback).ThenBy(p => p.Name).ToList(),
                AcceptsChildren = acceptsChildren,
                Namespace = t.Namespace,
                Library = packageId,
                Keywords = t.FullName ?? name,
                Summary = docs?.Type(t),
                TypeName = t.FullName,
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    public static string FriendlyName(Type t)
    {
        if (t.IsGenericParameter) return t.Name;
        if (t.FullName == "System.String") return "string";
        if (t.FullName == "System.Boolean") return "bool";
        if (t.FullName == "System.Int32") return "int";
        if (t.FullName == "System.Double") return "double";
        if (t.FullName == "System.Object") return "object";
        if (t.IsArray && t.GetElementType() is { } el) return FriendlyName(el) + "[]";
        if (t.IsGenericType)
        {
            // Verschachtelte Typen generischer Klassen haben oft keinen `n im Namen (Outer<T>.Inner)
            var tick = t.Name.IndexOf('`');
            var baseName = tick >= 0 ? t.Name[..tick] : t.Name;
            var args = string.Join(", ", t.GetGenericArguments().Select(FriendlyName));
            if (baseName == "Nullable") return args + "?";
            return tick >= 0 ? $"{baseName}<{args}>" : baseName;
        }
        return t.Name;
    }

    private static string Humanize(string name, string packageId)
    {
        // MudButton → Button, RadzenTextBox → TextBox
        var prefix = new string(packageId.TakeWhile(char.IsLetter).ToArray());
        foreach (var p in new[] { prefix, "Mud", "Radzen", "Fluent", "Syncfusion", "Sf", "Blazorise", "Ant", "Bit", "Rz" })
            if (p.Length > 1 && name.StartsWith(p, StringComparison.Ordinal) && name.Length > p.Length && char.IsUpper(name[p.Length]))
                return name[p.Length..];
        return name;
    }

    private static bool TextLike(string n) =>
        Has(n, "Button", "Text", "Link", "Chip", "Alert", "Badge", "Title", "Label", "Tab", "MenuItem", "Tooltip", "Heading");

    private static bool Has(string n, params string[] parts) => parts.Any(p => n.Contains(p, StringComparison.Ordinal));

    private static string CategoryOf(string n) => n switch
    {
        _ when Has(n, "Button", "Fab", "Icon", "Chip", "Toggle") => "actions",
        _ when Has(n, "Field", "Input", "TextBox", "Select", "Check", "Radio", "Switch", "Slider", "Picker", "Autocomplete", "Form", "Upload", "Rating", "Numeric", "Dropdown", "Combo") => "inputs",
        _ when Has(n, "Grid", "Table", "Data", "List", "Tree", "Virtual") => "data",
        _ when Has(n, "Nav", "Menu", "Tab", "Breadcrumb", "Link", "Drawer", "AppBar", "Toolbar", "Pager", "Pagination", "Stepper") => "navigation",
        _ when Has(n, "Dialog", "Popover", "Snackbar", "Alert", "Tooltip", "Progress", "Skeleton", "Overlay", "Toast", "Notification", "Message", "Badge") => "feedback",
        _ when Has(n, "Card", "Paper", "Stack", "Container", "Layout", "Item", "Spacer", "Divider", "Panel", "Expansion", "Collapse", "Row", "Column", "Splitter", "Main", "Flex") => "layout",
        _ when Has(n, "Text", "Title", "Heading", "Typography", "Label", "Markdown", "Highlighter") => "text",
        _ when Has(n, "Chart", "Image", "Avatar", "Carousel", "Timeline", "Calendar", "Scheduler", "Map") => "media",
        _ => "other",
    };

    private static string IconOf(string n) => n switch
    {
        _ when Has(n, "Button", "Fab") => "button",
        _ when Has(n, "Check", "Switch", "Toggle") => "checkbox",
        _ when Has(n, "Select", "Autocomplete", "Dropdown", "Combo") => "select",
        _ when Has(n, "Field", "Input", "TextBox", "Numeric", "Picker") => "input",
        _ when Has(n, "Grid", "Table", "Data") => "grid",
        _ when Has(n, "List", "Tree") => "list",
        _ when Has(n, "Card", "Paper") => "card",
        _ when Has(n, "Stack", "Flex", "Row") => "row",
        _ when Has(n, "Nav", "Menu", "Link", "Breadcrumb") => "link",
        _ when Has(n, "Dialog", "Popover", "Overlay", "Drawer") => "frame",
        _ when Has(n, "Image", "Avatar", "Carousel") => "image",
        _ when Has(n, "Text", "Typography", "Title", "Heading", "Label") => "text",
        _ when Has(n, "Icon") => "sparkle",
        _ when Has(n, "Chart") => "activity",
        _ when Has(n, "Form") => "form",
        _ => "puzzle",
    };

    /// <summary>DLLs der .NET- und ASP.NET-Core-Laufzeit für die Typauflösung.</summary>
    private static IEnumerable<string> FrameworkAssemblies()
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        foreach (var f in Directory.GetFiles(runtimeDir, "*.dll")) yield return f;
        // …/shared/Microsoft.NETCore.App/10.0.x → …/shared/Microsoft.AspNetCore.App/<neueste>
        var shared = Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir.TrimEnd(Path.DirectorySeparatorChar)));
        var aspnet = shared is null ? null : Path.Combine(shared, "Microsoft.AspNetCore.App");
        if (aspnet is not null && Directory.Exists(aspnet))
        {
            var latest = Directory.GetDirectories(aspnet).OrderByDescending(d => Path.GetFileName(d), Comparer<string>.Create(NuGet.NuGetClient.CompareVersions)).FirstOrDefault();
            if (latest is not null) foreach (var f in Directory.GetFiles(latest, "*.dll")) yield return f;
        }
    }
}

/// <summary>XML-Dokumentation einer Paket-DLL (MudBlazor.xml …): Zusammenfassungen von Typen und Properties.</summary>
internal sealed class XmlDocs
{
    private readonly Dictionary<string, string> _members = new(StringComparer.Ordinal);

    public static XmlDocs? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var docs = new XmlDocs();
            var x = System.Xml.Linq.XDocument.Load(path);
            foreach (var m in x.Descendants("member"))
            {
                var name = (string?)m.Attribute("name");
                var summary = m.Element("summary");
                if (name is null || summary is null) continue;
                var text = string.Join(" ", summary.DescendantNodes().Select(n => n switch
                {
                    System.Xml.Linq.XText t => t.Value,
                    System.Xml.Linq.XElement e when e.Name == "see" || e.Name == "seealso" => ((string?)e.Attribute("cref") ?? (string?)e.Attribute("langword") ?? "").Split('.').Last().Split('`')[0],
                    System.Xml.Linq.XElement e when e.Name == "paramref" || e.Name == "typeparamref" => (string?)e.Attribute("name") ?? "",
                    _ => "",
                }).Where(s => s.Length > 0));
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
                if (text.Length > 0) docs._members[name] = text;
            }
            return docs;
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? Type(Type t) => t.FullName is { } n && _members.TryGetValue("T:" + n, out var s) ? s : null;

    public string? Property(Type declaring, string name)
    {
        for (var t = declaring; t is not null; t = SafeBase(t))
        {
            if (t.FullName is { } n && _members.TryGetValue("P:" + n + "." + name, out var s)) return s;
            // generische Basistypen: P:MudBlazor.MudBaseInput`1.Label
            if (t.IsGenericType && t.GetGenericTypeDefinition().FullName is { } g && _members.TryGetValue("P:" + g + "." + name, out var gs)) return gs;
        }
        return null;
    }

    private static Type? SafeBase(Type t)
    {
        try { return t.BaseType; }
        catch (Exception e) when (e is FileNotFoundException or TypeLoadException) { return null; }
    }
}
