namespace Acme.Billing.Data.Infrastructure;

public enum InvoiceStatus { Draft, Issued, Paid, Overdue, Written_Off }

public class InvoiceModel
{
    /// <summary>Int identity — exercises DbGenerated key capture into the manifest (handoff §7).</summary>
    public int Id { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public Guid AccountId { get; set; }
    public AccountModel? Account { get; set; }
    /// <summary>
    /// External reference: the CRM/Orders domain owns Customer. This column agrees with the
    /// owning domain's data via the TDM identity contract — no cross-database coordination.
    /// </summary>
    public Guid CustomerId { get; set; }
    public decimal Amount { get; set; }
    public DateTime IssuedDate { get; set; }
    public InvoiceStatus Status { get; set; }
}
