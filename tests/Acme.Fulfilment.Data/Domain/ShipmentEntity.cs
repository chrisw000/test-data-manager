namespace Acme.Fulfilment.Data.Domain;

/// <summary>Enum-heavy state flow; weighted statistical config drives realistic mixes.</summary>
public enum ShipmentStatus { Created, Picking, Packed, Dispatched, InTransit, Delivered, Exception }

public class ShipmentEntity
{
    /// <summary>Server-assigned <see langword="long"/> identity — exercises DbGenerated key
    /// capture into the manifest. The natural key for references is <see cref="ShipmentNumber"/>.</summary>
    public long Id { get; set; }
    public string ShipmentNumber { get; set; } = "";

    /// <summary>External reference: the Orders domain owns Order. This column agrees with
    /// Orders via the identity contract — the third link in the Orders → Billing →
    /// Fulfilment chain, with no cross-database coordination.</summary>
    public Guid OrderId { get; set; }

    public ShipmentStatus Status { get; set; }

    /// <summary>Delivery window — DateOnly + two TimeOnly columns.</summary>
    public DateOnly DeliveryDate { get; set; }
    public TimeOnly WindowStart { get; set; }
    public TimeOnly WindowEnd { get; set; }

    /// <summary>Correlated pair (carrier ↔ service level), filled from one sampled dataset row.</summary>
    public string Carrier { get; set; } = "";
    public string ServiceLevel { get; set; } = "";

    /// <summary>Dispatch bin — FK into the self-referencing <see cref="LocationEntity"/> tree.</summary>
    public Guid DispatchBinId { get; set; }
    public LocationEntity? DispatchBin { get; set; }
}
