namespace Acme.Billing.Data.Infrastructure;

public class AccountModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Currency { get; set; } = "GBP";
    public decimal Balance { get; set; }
    public bool Active { get; set; } = true;
}
