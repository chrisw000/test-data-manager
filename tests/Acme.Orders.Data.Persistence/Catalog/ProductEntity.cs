// Deliberately "defined elsewhere in the domain layer": this model lives outside the
// conventional Data.Persistence.Domain namespace. TDM still discovers it because entity
// discovery is EF-model-first (the compiled DbContext model), not namespace-based.
namespace Acme.Orders.Domain.Catalog;

public class ProductEntity
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string Category { get; set; } = "";
    public bool Discontinued { get; set; }
}
