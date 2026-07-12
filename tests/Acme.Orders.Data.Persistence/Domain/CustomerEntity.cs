namespace Acme.Orders.Data.Persistence.Domain;

public class CustomerEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Tier { get; set; } = "Standard";
    public string Email { get; set; } = "";
    public decimal CreditLimit { get; set; }
    public DateTime CreatedUtc { get; set; }
    public ICollection<OrderEntity> Orders { get; set; } = [];
}
