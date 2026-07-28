using Microsoft.EntityFrameworkCore;

namespace Bobcat.EntityFrameworkCore.Tests;

/// <summary>A record entity — columns bind to the primary constructor.</summary>
public record Customer(string Name, string Region, int Orders)
{
    public int Id { get; set; }
}

public class ShopContext : DbContext
{
    public ShopContext(DbContextOptions<ShopContext> options) : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();
}
