using DemoShop.Models;
using Microsoft.EntityFrameworkCore;

namespace DemoShop.Data;

public class ShopDbContext : DbContext
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (!options.IsConfigured) options.UseSqlite("Data Source=shop.db");
    }
}
