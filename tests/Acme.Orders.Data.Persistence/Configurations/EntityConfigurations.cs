using Acme.Orders.Data.Persistence.Domain;
using Acme.Orders.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Orders.Data.Persistence.Configurations;

// Schema mapping lives in IEntityTypeConfiguration<T> classes — one per table, per the
// team convention. TDM consumes these through the compiled DbContext model.

public sealed class CustomerEntityConfiguration : IEntityTypeConfiguration<CustomerEntity>
{
    public void Configure(EntityTypeBuilder<CustomerEntity> customer)
    {
        customer.Property(c => c.Name).IsRequired().HasMaxLength(200);
        customer.Property(c => c.Tier).HasMaxLength(50);
    }
}

public sealed class ProductEntityConfiguration : IEntityTypeConfiguration<ProductEntity>
{
    public void Configure(EntityTypeBuilder<ProductEntity> product)
    {
        product.Property(p => p.Sku).IsRequired().HasMaxLength(50);
    }
}

public sealed class OrderEntityConfiguration : IEntityTypeConfiguration<OrderEntity>
{
    public void Configure(EntityTypeBuilder<OrderEntity> order)
    {
        order.Property(o => o.OrderNumber).IsRequired().HasMaxLength(50);
        order.HasOne(o => o.Customer)
            .WithMany(c => c.Orders)
            .HasForeignKey(o => o.CustomerId);
    }
}

/// <summary>Deliberately never applied in OrdersDbContext — see <see cref="WarehouseEntity"/>.</summary>
public sealed class WarehouseEntityConfiguration : IEntityTypeConfiguration<WarehouseEntity>
{
    public void Configure(EntityTypeBuilder<WarehouseEntity> warehouse)
    {
        warehouse.Property(w => w.Name).IsRequired().HasMaxLength(100);
    }
}
