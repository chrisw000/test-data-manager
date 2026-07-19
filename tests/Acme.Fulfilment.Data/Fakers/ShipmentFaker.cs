using Acme.Fulfilment.Data.Domain;
using Bogus;

namespace Acme.Fulfilment.Data.Fakers;

/// <summary>Generates the delivery window (DateOnly + two TimeOnly). Status, Carrier and
/// ServiceLevel are left to the statistical layer (weights + a correlated dataset) so the
/// guide can show config-driven shape over faker output. Deterministic under the seed.</summary>
public class ShipmentFaker : Faker<ShipmentEntity>
{
    public ShipmentFaker()
    {
        RuleFor(s => s.ShipmentNumber, f => $"SHP-{f.IndexFaker + 1:D5}");
        RuleFor(s => s.DeliveryDate, f => DateOnly.FromDateTime(f.Date.Soon(14)));
        RuleFor(s => s.WindowStart, f => new TimeOnly(f.PickRandom(8, 9, 10, 12, 14), 0));
        RuleFor(s => s.WindowEnd, (f, s) => s.WindowStart.AddHours(2));
    }
}
