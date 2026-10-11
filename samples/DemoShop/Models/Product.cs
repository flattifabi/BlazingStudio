namespace DemoShop.Models;

public enum CardVariant { Default, Compact, Wide }

public sealed class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public decimal Price { get; set; }

    public bool IsActive { get; set; } = true;
}
