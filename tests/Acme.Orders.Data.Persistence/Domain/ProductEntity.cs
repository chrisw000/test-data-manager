namespace Acme.Orders.Data.Persistence.Domain;

public class ProductEntity
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string Category { get; set; } = "";
    public bool Discontinued { get; set; }
}
