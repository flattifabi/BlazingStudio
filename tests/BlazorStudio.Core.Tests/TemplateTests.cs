using BlazorStudio.Core.Templates;
using Xunit;

namespace BlazorStudio.Core.Tests;

public class TemplateTests
{
    [Theory]
    [InlineData("my-page 2", "MyPage2")]
    [InlineData("Product", "Product")]
    [InlineData("1st", "_1st")]
    [InlineData("order_item", "Order_item")]
    public void Builds_identifiers(string name, string expected) => Assert.Equal(expected, FileTemplates.ToIdentifier(name));

    [Fact]
    public void File_names_get_extension_once()
    {
        var cls = FileTemplates.Find("class")!;
        Assert.Equal("Product.cs", FileTemplates.FileName(cls, "Product"));
        Assert.Equal("Product.cs", FileTemplates.FileName(cls, "Product.cs"));
        var cb = FileTemplates.Find("codebehind")!;
        Assert.Equal("Card.razor.cs", FileTemplates.FileName(cb, "Card"));
        Assert.Equal("Card.razor.cs", FileTemplates.FileName(cb, "Card.razor"));
        Assert.Equal(".gitignore", FileTemplates.FileName(FileTemplates.Find("gitignore")!, ""));
    }

    [Fact]
    public void Class_uses_file_scoped_namespace()
    {
        var f = Assert.Single(FileTemplates.Generate(FileTemplates.Find("class")!, "Product", new TemplateOptions { Namespace = "Shop.Models" }));
        Assert.Equal("Product.cs", f.RelativePath);
        Assert.Contains("namespace Shop.Models;", f.Content);
        Assert.Contains("public class Product", f.Content);
    }

    [Fact]
    public void Page_with_code_behind_and_css()
    {
        var files = FileTemplates.Generate(FileTemplates.Find("page")!, "OrderList", new TemplateOptions { Namespace = "Shop.Pages", CodeBehind = true, ScopedCss = true });
        Assert.Equal(new[] { "OrderList.razor", "OrderList.razor.cs", "OrderList.razor.css" }, files.Select(f => f.RelativePath));
        Assert.Contains("@page \"/order-list\"", files[0].Content);
        Assert.DoesNotContain("@code", files[0].Content);
        Assert.Contains("public partial class OrderList", files[1].Content);
    }

    [Fact]
    public void Service_creates_interface_and_implementation()
    {
        var f = Assert.Single(FileTemplates.Generate(FileTemplates.Find("service")!, "CartService", new TemplateOptions { Namespace = "Shop" }));
        Assert.Contains("public interface ICartService", f.Content);
        Assert.Contains("public sealed class CartService : ICartService", f.Content);
    }

    [Fact]
    public void Namespace_follows_folders()
    {
        Assert.Equal("Shop.Components.Shared", FileTemplates.NamespaceFor("Shop", @"C:\p", @"C:\p\Components\Shared"));
        Assert.Equal("Shop", FileTemplates.NamespaceFor("Shop", @"C:\p", @"C:\p"));
        Assert.Equal("Shop.My_Folder._2024", FileTemplates.NamespaceFor("Shop", @"C:\p", @"C:\p\My-Folder\2024"));
    }
}
