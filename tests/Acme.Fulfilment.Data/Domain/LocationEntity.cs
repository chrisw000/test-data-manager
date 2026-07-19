namespace Acme.Fulfilment.Data.Domain;

public enum LocationKind { Site, Aisle, Bin }

/// <summary>
/// Self-referencing warehouse hierarchy: a Site contains Aisles, an Aisle contains Bins.
/// The parent link is a nullable self-FK (a Site has no parent) — TDM resolves a
/// "for Location &quot;SITE-1&quot;" reference to the parent's deterministic id.
/// </summary>
public class LocationEntity
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public LocationKind Kind { get; set; }

    public Guid? ParentId { get; set; }
    public LocationEntity? Parent { get; set; }
    public ICollection<LocationEntity> Children { get; set; } = [];
}
