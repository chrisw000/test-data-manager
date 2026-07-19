using Acme.Fulfilment.Data.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Fulfilment.Data.Configurations;

public sealed class LocationEntityConfiguration : IEntityTypeConfiguration<LocationEntity>
{
    public void Configure(EntityTypeBuilder<LocationEntity> location)
    {
        location.Property(l => l.Code).IsRequired().HasMaxLength(50);
        location.Property(l => l.Name).IsRequired().HasMaxLength(120);
        // Self-referencing hierarchy: parent is optional (a Site has none).
        location.HasOne(l => l.Parent)
            .WithMany(l => l.Children)
            .HasForeignKey(l => l.ParentId)
            .IsRequired(false);
    }
}

public sealed class ShipmentEntityConfiguration : IEntityTypeConfiguration<ShipmentEntity>
{
    public void Configure(EntityTypeBuilder<ShipmentEntity> shipment)
    {
        // Server-assigned long identity — the manifest records the assigned key.
        shipment.Property(s => s.Id).ValueGeneratedOnAdd();
        shipment.Property(s => s.ShipmentNumber).IsRequired().HasMaxLength(50);
        shipment.Property(s => s.Carrier).HasMaxLength(60);
        shipment.Property(s => s.ServiceLevel).HasMaxLength(60);
        shipment.HasOne(s => s.DispatchBin)
            .WithMany()
            .HasForeignKey(s => s.DispatchBinId);
        // OrderId is deliberately NOT a mapped relationship: Order lives in the Orders
        // domain's database. It is populated via the identity contract.
    }
}
