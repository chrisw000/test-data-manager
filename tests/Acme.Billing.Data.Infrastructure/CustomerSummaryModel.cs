namespace Acme.Billing.Data.Infrastructure;

/// <summary>
/// Read-model projection of the externally-owned Customer — the eventually-consistent local
/// copy messaging would normally produce. Seeded by the TDM under Projection behaviour with
/// the owning domain's derived PK (handoff §8.5).
/// </summary>
public class CustomerSummaryModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Tier { get; set; } = "";
}
