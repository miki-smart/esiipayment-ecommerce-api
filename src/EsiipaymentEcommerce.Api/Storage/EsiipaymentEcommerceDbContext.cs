using EsiipaymentEcommerce.Api.Payment;
using Microsoft.EntityFrameworkCore;

namespace EsiipaymentEcommerce.Api.Storage;

public sealed class EsiipaymentEcommerceDbContext(DbContextOptions<EsiipaymentEcommerceDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<PaymentRecord> Payments => Set<PaymentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>().HasKey(o => o.Id);
        modelBuilder.Entity<PaymentRecord>().HasKey(p => p.IdempotencyKey);

        modelBuilder.Entity<Product>().HasData(
            new Product { Id = 1, Name = "Habesha Kemis", Description = "Traditional handwoven cotton dress.", PriceMinorUnits = 350000, Currency = "ETB" },
            new Product { Id = 2, Name = "Jebena Coffee Set", Description = "Clay coffee pot with two cups.", PriceMinorUnits = 120000, Currency = "ETB" },
            new Product { Id = 3, Name = "Ethiopian Coffee Beans, 1kg", Description = "Single-origin Yirgacheffe, medium roast.", PriceMinorUnits = 45000, Currency = "ETB" });
    }
}
