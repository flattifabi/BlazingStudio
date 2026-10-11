using BlazorStudio.Core.Catalog;
using BlazorStudio.Core.Completion;
using BlazorStudio.Core.Editing;
using BlazorStudio.Core.Model;
using BlazorStudio.Core.Razor;
using BlazorStudio.Core.Styles;

namespace BlazorStudio.Core.Tests;

public class RazorRoundTripTests
{
    private const string Sample = """
        @page "/"
        @inject ProductService Products

        <PageTitle>Home</PageTitle>

        <section class="hero">
            <h1>Willkommen im Shop</h1>
            <p>Hallo @userName, schön dass du da bist.</p>
            <div class="actions">
                <button class="btn btn-primary" @onclick="GoToShop">Jetzt einkaufen</button>
                <button class="btn" disabled>Mehr</button>
            </div>
        </section>

        @foreach (var p in products)
        {
            <ProductCard Product="p" ShowPrice="true" />
        }

        @if (isLoading)
        {
            <p>Lade…</p>
        }
        else
        {
            <p>Fertig</p>
        }

        @code {
            private List<Product> products = new();
            private bool isLoading;
            private string userName = "Gast";

            private void GoToShop() { }
        }
        """;

    [Fact]
    public void Parses_directives_markup_and_code()
    {
        var r = RazorParser.Parse(Sample);
        Assert.False(r.HasErrors, string.Join(", ", r.Diagnostics));
        Assert.Equal(2, r.Document.Directives.Count);
        Assert.Equal("/", r.Document.PageRoute);
        Assert.Single(r.Document.CodeBlocks);

        var top = r.Document.Root.Children;
        Assert.Equal("PageTitle", top[0].Name);
        Assert.Equal(NodeKind.Component, top[0].Kind);
        Assert.Equal("section", top[1].Name);
        Assert.Equal(NodeKind.Control, top[2].Kind);
        Assert.Equal("foreach (var p in products)", top[2].Text);
        Assert.Equal("ProductCard", top[2].Children.Single().Name);
        Assert.Equal("if", top[3].Name);
        Assert.True(top[4].IsContinuation);
        Assert.Equal("else", top[4].Text);
    }

    [Fact]
    public void Round_trip_is_stable()
    {
        var first = RazorWriter.Write(RazorParser.Parse(Sample).Document).Text;
        var second = RazorWriter.Write(RazorParser.Parse(first).Document).Text;
        Assert.Equal(first, second);
        Assert.Contains("<button class=\"btn btn-primary\" @onclick=\"GoToShop\">Jetzt einkaufen</button>", first);
        Assert.Contains("<p>Hallo @userName, schön dass du da bist.</p>", first);
        Assert.Contains("else\n{", first);
        Assert.Contains("private void GoToShop() { }", first);
    }

    [Fact]
    public void Keeps_csharp_inside_control_blocks()
    {
        const string src = """
            @foreach (var item in items)
            {
                var label = item.Name.ToUpper();
                <li>@label</li>
            }
            """;
        var doc = RazorParser.Parse(src).Document;
        var loop = doc.Root.Children.Single();
        Assert.Equal(NodeKind.Code, loop.Children[0].Kind);
        Assert.Equal("li", loop.Children[1].Name);
        var text = RazorWriter.Write(doc).Text;
        Assert.Contains("    var label = item.Name.ToUpper();\n    <li>@label</li>", text);
    }

    [Fact]
    public void Attribute_with_implicit_expression_containing_strings()
    {
        const string src = """<link rel="stylesheet" href="@Assets["lib/bootstrap/dist/css/bootstrap.min.css"]" />""" + "\n" + """<span title="@L["key"].ToUpper()">x</span>""";
        var r = RazorParser.Parse(src);
        Assert.False(r.HasErrors);
        Assert.Equal("@Assets[\"lib/bootstrap/dist/css/bootstrap.min.css\"]", r.Document.Root.Children[0].GetAttribute("href"));
        Assert.Equal("@L[\"key\"].ToUpper()", r.Document.Root.Children[1].GetAttribute("title"));
        Assert.Equal(src, RazorWriter.Write(r.Document).Text.Trim());
    }

    [Fact]
    public void Attribute_with_razor_expression_and_quotes()
    {
        const string src = """<div class="@(active ? "on" : "off") box" @onclick="() => count++">x</div>""";
        var r = RazorParser.Parse(src);
        Assert.False(r.HasErrors);
        var div = r.Document.Root.Children.Single();
        Assert.Equal("@(active ? \"on\" : \"off\") box", div.GetAttribute("class"));
        Assert.Equal("() => count++", div.GetAttribute("@onclick"));
        Assert.Equal(src, RazorWriter.Write(r.Document).Text.Trim());
    }

    [Fact]
    public void Apostrophe_in_markup_inside_block_does_not_break()
    {
        const string src = "@if (show)\n{\n    <p>Don't stop</p>\n}\n<span>after</span>";
        var r = RazorParser.Parse(src);
        Assert.False(r.HasErrors);
        Assert.Equal(2, r.Document.Root.Children.Count);
        Assert.Equal("span", r.Document.Root.Children[1].Name);
    }

    [Fact]
    public void Generic_types_in_code_block_are_not_markup()
    {
        const string src = "@{\n    var list = new List<string>();\n}\n<p>ok</p>";
        var r = RazorParser.Parse(src);
        Assert.False(r.HasErrors);
        Assert.Equal(NodeKind.Code, r.Document.Root.Children[0].Kind);
        Assert.Equal("p", r.Document.Root.Children[1].Name);
    }

    [Fact]
    public void Reports_unclosed_tags()
    {
        var r = RazorParser.Parse("<div><span>text</div>");
        Assert.Contains(r.Diagnostics, d => d.Code == RazorDiagnosticCode.UnclosedTag && d.Argument == "span");
    }

    [Fact]
    public void Writer_spans_point_at_nodes()
    {
        var doc = RazorParser.Parse("<div>\n<p>Hi</p>\n</div>").Document;
        var w = RazorWriter.Write(doc);
        var p = doc.Root.Children[0].Children[0];
        var (s, e) = w.Spans[p.Id];
        Assert.Equal("<p>Hi</p>", w.Text[s..e]);
        Assert.Equal(p, DocumentEditor.NodeAt(doc, w.Spans, s + 3));
    }

    [Fact]
    public void Insert_move_delete()
    {
        var doc = RazorParser.Parse("<div></div><p>a</p>").Document;
        var div = doc.Root.Children[0];
        var p = doc.Root.Children[1];
        var btn = DocumentEditor.InsertTemplate(doc, "<button type=\"button\">B</button>", div, DropPosition.Inside)!;
        Assert.Equal(div, btn.Parent);
        Assert.True(DocumentEditor.Move(doc, p, btn, DropPosition.Before));
        Assert.Equal(new[] { "p", "button" }, div.Children.Select(c => c.Name));
        Assert.False(DocumentEditor.Move(doc, div, btn, DropPosition.Inside));
        DocumentEditor.Delete(btn);
        Assert.Single(div.Children);
    }

    [Fact]
    public void Style_and_box_shorthand()
    {
        var s = StyleDeclarations.Parse("padding: 10px 18px; background: url(a;b.png); color: red");
        Assert.Equal("url(a;b.png)", s.Get("background"));
        var box = BoxValues.From(s, "padding");
        Assert.Equal("18px", box.Left);
        box.With(0, "4px").ApplyTo(s, "padding");
        Assert.Equal("4px 18px 10px", s.Get("padding"));
        new BoxValues("8px", "8px", "8px", "8px").ApplyTo(s, "margin");
        Assert.Equal("8px", s.Get("margin"));
    }

    [Fact]
    public void Css_scoper_and_index()
    {
        const string css = ":root { --primary: #123456; } body { margin: 0 } .btn, .card > h3 { color: red } @media (min-width: 600px) { .grid { display: grid } }";
        var scoped = CssScoper.Scope(css, ".bs-canvas");
        Assert.Contains(".bs-canvas{ --primary: #123456; }", scoped);
        Assert.Contains(".bs-canvas .btn, .bs-canvas .card > h3", scoped);
        Assert.Contains("@container (min-width: 600px){", scoped);
        var index = new CssIndex();
        index.AddCss(css, "app.css");
        Assert.Contains(index.Classes, c => c.Name == "grid");
        Assert.Contains(index.Tokens, t => t.Name == "--primary" && t.IsColor);
    }

    [Fact]
    public void Code_inspector_reads_parameters_and_methods()
    {
        const string code = """
            @code {
                /// <summary>Das Produkt.</summary>
                [Parameter, EditorRequired] public Product Product { get; set; } = default!;
                [Parameter] public bool ShowPrice { get; set; }
                [Parameter] public EventCallback<Product> OnAddToCart { get; set; }
                private int count;
                private async Task AddAsync(Product p) { }
            }
            """;
        var members = CodeInspector.AnalyzeCodeBlock(code);
        var product = members.Single(m => m.Name == "Product");
        Assert.True(product.IsParameter);
        Assert.True(product.IsRequired);
        Assert.Equal("Das Produkt.", product.Summary);
        Assert.Contains(members, m => m.Name == "AddAsync" && m.Kind == CodeMemberKind.Method);
        Assert.Contains(members, m => m.Name == "count" && m.Kind == CodeMemberKind.Field);

        var blocks = CodeInspector.AddMethodToCodeBlocks(new[] { code }, CodeInspector.CreateHandlerStub("OnBuy", "MouseEventArgs", false));
        Assert.Contains("private void OnBuy(MouseEventArgs e)", blocks[0]);
        Assert.EndsWith("}", blocks[0].TrimEnd());
    }

    [Fact]
    public void Binding_scope_finds_loop_variables_with_types()
    {
        var doc = RazorParser.Parse("@foreach (var p in products)\n{\n<ProductCard />\n}").Document;
        var card = doc.Root.Children[0].Children[0];
        var members = new[] { new CodeMember("products", "List<Product>", CodeMemberKind.Field, false, false, false, false, null, null, "@code") };
        var scope = BindingScope.Collect(doc, card, members);
        var p = scope.Single(c => c.Expression == "p");
        Assert.Equal("Product", p.Type);
        Assert.Equal("p", BindingScope.ForValue(scope, "Product").First().Expression);
    }

    [Fact]
    public void Completion_suggests_classes_parameters_and_events()
    {
        var css = new CssIndex();
        css.AddCss(".btn-primary{color:red}.btn-pill{}", "app.css");
        var comp = new ComponentDefinition
        {
            Key = "project:ProductCard", TagName = "ProductCard", Label = "ProductCard", Category = "project", Template = "<ProductCard />",
            Source = ComponentSource.Project,
            Parameters = new[] { new ParameterDefinition("ShowPrice", "bool", ValueEditor.Bool) },
        };
        var members = new[] { new CodeMember("GoToShop", "void", CodeMemberKind.Method, false, false, false, false, "()", null, "@code") };

        var text = "<button class=\"btn btn-p";
        var r = CompletionEngine.Complete(new CompletionContext { Text = text, Offset = text.Length, Css = css });
        Assert.Equal(5, r.ReplaceLength);
        Assert.Contains(r.Items, i => i.Label == "btn-primary");

        text = "<ProductCard Sh";
        r = CompletionEngine.Complete(new CompletionContext { Text = text, Offset = text.Length, Components = new[] { comp } });
        Assert.Contains(r.Items, i => i.Label == "ShowPrice");

        text = "<button @onclick=\"Go";
        r = CompletionEngine.Complete(new CompletionContext { Text = text, Offset = text.Length, Members = members });
        Assert.Contains(r.Items, i => i.Label == "GoToShop");

        text = "<div style=\"display: fl";
        r = CompletionEngine.Complete(new CompletionContext { Text = text, Offset = text.Length });
        Assert.Contains(r.Items, i => i.Label == "flex");
        Assert.Equal(2, r.ReplaceLength);
    }
}
