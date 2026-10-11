using DemoShop.Models;

namespace DemoShop.Components.Pages;

public partial class Home
{
    private readonly List<Product> products = new()
    {
        new Product { Id = 1, Name = "Bergkäse", Description = "12 Monate gereift", Price = 8.90m },
        new Product { Id = 2, Name = "Zirbenhonig", Description = "Aus Tirol", Price = 11.50m },
        new Product { Id = 3, Name = "Kürbiskernöl", Description = "Steirisch g.g.A.", Price = 14.00m },
    };

    private int cartCount;

    private bool IsLoading { get; set; }

    public Product? FeaturedProduct => products.FirstOrDefault();

    private void GoToShop()
    {
    }

    private void AddToCart(Product product)
    {
        cartCount++;
        StateHasChanged();
    }

    private void OnButtonMouseup(MouseEventArgs e)
    {
        e.FeaturedProduct
    }
}
