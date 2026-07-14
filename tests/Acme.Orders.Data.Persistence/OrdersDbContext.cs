using Acme.Orders.Data.Persistence.Configurations;
using Acme.Orders.Data.Persistence.Domain;
using Acme.Orders.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Acme.Orders.Data.Persistence;

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<CustomerEntity> Customers => Set<CustomerEntity>();
    public DbSet<ProductEntity> Products => Set<ProductEntity>();
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Per-table IEntityTypeConfiguration classes, applied one by one.
        // WarehouseEntityConfiguration is deliberately missing — TDM's configuration
        // cross-check surfaces exactly this mistake as a warning.
        modelBuilder.ApplyConfiguration(new CustomerEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ProductEntityConfiguration());
        modelBuilder.ApplyConfiguration(new OrderEntityConfiguration());
    }
}
