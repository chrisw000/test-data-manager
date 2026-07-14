namespace Acme.Orders.Data.Persistence.Domain;

/// <summary>
/// Has an IEntityTypeConfiguration (WarehouseEntityConfiguration) that is deliberately never
/// applied in OrdersDbContext — the classic missed-registration mistake. TDM's configuration
/// cross-check discovers it and warns; the type stays usable for generation only.
/// </summary>
public class WarehouseEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Region { get; set; } = "";
}
