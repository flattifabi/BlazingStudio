using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BlazorStudio.Core.Razor;

public enum CodeMemberKind { Field, Property, Method }

public sealed record CodeMember(
    string Name,
    string Type,
    CodeMemberKind Kind,
    bool IsParameter,
    bool IsCascading,
    bool IsRequired,
    bool IsStatic,
    string? Signature,
    string? Summary,
    string Source)
{
    public bool IsEventCallback => Type.StartsWith("EventCallback", StringComparison.Ordinal);

    public bool IsRenderFragment => Type.StartsWith("RenderFragment", StringComparison.Ordinal);

    /// <summary>Typ ohne Nullable-Markierung.</summary>
    public string BareType => Type.TrimEnd('?');
}

public sealed record EnumInfo(string Name, IReadOnlyList<string> Values);

/// <summary>Liest Member aus @code-Blöcken und Code-Behind-Dateien (Roslyn, nur Syntax).</summary>
public static class CodeInspector
{
    /// <summary>codeBlocks: komplette "@code { ... }"-Texte oder reiner C#-Klasseninhalt.</summary>
    public static IReadOnlyList<CodeMember> AnalyzeCodeBlock(string codeBlocks, string source = "@code")
    {
        var body = StripCodeBlockWrappers(codeBlocks);
        var tree = CSharpSyntaxTree.ParseText("partial class __Component {\n" + body + "\n}");
        var cls = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        return cls is null ? Array.Empty<CodeMember>() : ReadMembers(cls, source);
    }

    /// <summary>Liest die Member einer (partial) Klasse aus einer .cs-Datei.</summary>
    public static IReadOnlyList<CodeMember> AnalyzeFile(string csharp, string? className, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(csharp);
        var classes = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(c => className is null || c.Identifier.Text == className)
            .ToList();
        return classes.SelectMany(c => ReadMembers(c, source)).ToList();
    }

    public static IReadOnlyList<EnumInfo> FindEnums(string csharp)
    {
        var tree = CSharpSyntaxTree.ParseText(csharp);
        return tree.GetRoot().DescendantNodes().OfType<EnumDeclarationSyntax>()
            .Select(e => new EnumInfo(e.Identifier.Text, e.Members.Select(m => m.Identifier.Text).ToList()))
            .ToList();
    }

    public static IReadOnlyList<string> FindMethods(string codeBlocks) =>
        AnalyzeCodeBlock(codeBlocks).Where(m => m.Kind == CodeMemberKind.Method).Select(m => m.Name).Distinct().ToList();

    public static IReadOnlyList<string> FindMembers(string codeBlocks) =>
        AnalyzeCodeBlock(codeBlocks).Where(m => m.Kind != CodeMemberKind.Method).Select(m => m.Name).Distinct().ToList();

    private static string StripCodeBlockWrappers(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith('@')) return t;
        var parts = new List<string>();
        var i = 0;
        while (i < t.Length)
        {
            var at = t.IndexOf('@', i);
            if (at < 0) break;
            var brace = t.IndexOf('{', at);
            if (brace < 0) break;
            var end = CSharpScanner.SkipBalanced(t, brace, '{', '}');
            if (end < 0) end = t.Length;
            var innerEnd = Math.Max(brace + 1, end - 1);
            parts.Add(t[(brace + 1)..innerEnd]);
            i = end;
        }
        return string.Join('\n', parts);
    }

    private static List<CodeMember> ReadMembers(TypeDeclarationSyntax cls, string source)
    {
        var list = new List<CodeMember>();
        foreach (var m in cls.Members)
        {
            switch (m)
            {
                case PropertyDeclarationSyntax p:
                    list.Add(new CodeMember(
                        p.Identifier.Text, p.Type.ToString(), CodeMemberKind.Property,
                        HasAttribute(p.AttributeLists, "Parameter"),
                        HasAttribute(p.AttributeLists, "CascadingParameter"),
                        HasAttribute(p.AttributeLists, "EditorRequired") || p.Modifiers.Any(SyntaxKind.RequiredKeyword),
                        p.Modifiers.Any(SyntaxKind.StaticKeyword),
                        null, Summary(p), source));
                    break;
                case FieldDeclarationSyntax f:
                    foreach (var v in f.Declaration.Variables)
                    {
                        list.Add(new CodeMember(
                            v.Identifier.Text, f.Declaration.Type.ToString(), CodeMemberKind.Field,
                            false, false, false, f.Modifiers.Any(SyntaxKind.StaticKeyword) || f.Modifiers.Any(SyntaxKind.ConstKeyword),
                            null, Summary(f), source));
                    }
                    break;
                case MethodDeclarationSyntax md:
                    var name = md.Identifier.Text;
                    if (IsLifecycleMethod(name)) break;
                    list.Add(new CodeMember(
                        name, md.ReturnType.ToString(), CodeMemberKind.Method, false, false, false,
                        md.Modifiers.Any(SyntaxKind.StaticKeyword),
                        md.ParameterList.ToString(), Summary(md), source));
                    break;
            }
        }
        return list;
    }

    private static bool IsLifecycleMethod(string name) => name is
        "OnInitialized" or "OnInitializedAsync" or "OnParametersSet" or "OnParametersSetAsync" or
        "OnAfterRender" or "OnAfterRenderAsync" or "ShouldRender" or "SetParametersAsync" or "Dispose" or "DisposeAsync" or
        "BuildRenderTree";

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(l => l.Attributes).Any(a =>
        {
            var n = a.Name.ToString();
            return n == name || n == name + "Attribute" || n.EndsWith("." + name, StringComparison.Ordinal);
        });

    private static string? Summary(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia()
            .Select(t => t.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();
        if (trivia is null) return null;
        var summary = trivia.Content.OfType<XmlElementSyntax>().FirstOrDefault(e => e.StartTag.Name.ToString() == "summary");
        if (summary is null) return null;
        var text = string.Concat(summary.Content.Select(c => c.ToString())).Replace("///", "");
        return string.Join(' ', text.Split(new[] { '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries));
    }

    // ------------------------------------------------------------------
    // Code erzeugen
    // ------------------------------------------------------------------

    /// <summary>Fügt eine Methode in den letzten @code-Block ein bzw. legt einen an. Liefert die neuen Blöcke.</summary>
    public static List<string> AddMethodToCodeBlocks(IReadOnlyList<string> blocks, string methodSource)
    {
        var result = blocks.ToList();
        var method = Indent(methodSource.Trim(), "    ");
        if (result.Count == 0)
        {
            result.Add("@code {\n" + method + "\n}");
            return result;
        }
        var last = result[^1];
        var close = last.LastIndexOf('}');
        if (close < 0)
        {
            result.Add("@code {\n" + method + "\n}");
            return result;
        }
        var before = last[..close].TrimEnd();
        var sep = before.EndsWith('{') ? "\n" : "\n\n";
        result[^1] = before + sep + method + "\n}";
        return result;
    }

    /// <summary>Fügt eine Methode in die Klasse className einer .cs-Datei ein.</summary>
    public static string AddMethodToFile(string csharp, string className, string methodSource)
    {
        var tree = CSharpSyntaxTree.ParseText(csharp);
        var cls = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (cls is null) return csharp;
        var closePos = cls.CloseBraceToken.SpanStart;
        var lineStart = csharp.LastIndexOf('\n', Math.Max(0, closePos - 1)) + 1;
        var classIndent = csharp[lineStart..closePos];
        if (classIndent.Trim().Length > 0) classIndent = "";
        var memberIndent = classIndent + "    ";
        var before = csharp[..lineStart].TrimEnd('\r', '\n', ' ', '\t');
        var sep = before.EndsWith('{') ? "\n" : "\n\n";
        return before + sep + Indent(methodSource.Trim(), memberIndent) + "\n" + csharp[lineStart..];
    }

    /// <summary>Erzeugt den Rumpf einer Ereignismethode passend zum Event-Typ.</summary>
    public static string CreateHandlerStub(string methodName, string? argumentType, bool async)
    {
        var args = string.IsNullOrEmpty(argumentType) ? "" : $"{argumentType} e";
        return async
            ? $"private async Task {methodName}({args})\n{{\n    await Task.CompletedTask;\n}}"
            : $"private void {methodName}({args})\n{{\n}}";
    }

    public static string CreateFieldStub(string type, string name) => $"private {type} {name};";

    private static string Indent(string text, string pad) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(l => l.Length == 0 ? l : pad + l));
}
