using DemoShop.Models;
using Microsoft.AspNetCore.Components;

namespace DemoShop.Components.Shared;

public partial class ProductCard
{
    /// <summary>Das angezeigte Produkt.</summary>
    [Parameter, EditorRequired] public Product Product { get; set; } = default!;

    /// <summary>Überschreibt den Produktnamen als Titel.</summary>
    [Parameter] public string? Title { get; set; }

    /// <summary>Preis anzeigen?</summary>
    [Parameter] public bool ShowPrice { get; set; } = true;

    [Parameter] public CardVariant Variant { get; set; }

    [Parameter] public EventCallback<Product> OnAddToCart { get; set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }

    private string VariantClass => Variant switch
    {
        CardVariant.Compact => "compact",
        CardVariant.Wide => "wide",
        _ => "",
    };

    private Task AddAsync() => OnAddToCart.InvokeAsync(Product);
}
