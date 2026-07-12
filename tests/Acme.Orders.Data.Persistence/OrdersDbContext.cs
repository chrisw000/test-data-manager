using Acme.Orders.Data.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Acme.Orders.Data.Persistence;

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<CustomerEntity> Customers => Set<CustomerEntity>();
    public DbSet<ProductEntity> Products => Set<ProductEntity>();
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomerEntity>(customer =>
        {
            customer.Property(c => c.Name).IsRequired().HasMaxLength(200);
            customer.Property(c => c.Tier).HasMaxLength(50);
        });

        modelBuilder.Entity<ProductEntity>(product =>
        {
            product.Property(p => p.Sku).IsRequired().HasMaxLength(50);
        });

        modelBuilder.Entity<OrderEntity>(order =>
        {
            order.Property(o => o.OrderNumber).IsRequired().HasMaxLength(50);
            order.HasOne(o => o.Customer)
                .WithMany(c => c.Orders)
                .HasForeignKey(o => o.CustomerId);
        });
    }
}
