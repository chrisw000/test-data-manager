using Acme.Fulfilment.Data.Configurations;
using Acme.Fulfilment.Data.Domain;
using Microsoft.EntityFrameworkCore;

namespace Acme.Fulfilment.Data;

public class FulfilmentDbContext(DbContextOptions<FulfilmentDbContext> options) : DbContext(options)
{
    public DbSet<LocationEntity> Locations => Set<LocationEntity>();
    public DbSet<ShipmentEntity> Shipments => Set<ShipmentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new LocationEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ShipmentEntityConfiguration());
    }
}
