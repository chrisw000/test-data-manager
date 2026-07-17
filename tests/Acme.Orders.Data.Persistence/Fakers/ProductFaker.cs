using Acme.Orders.Domain.Catalog;
using Bogus;

namespace Acme.Orders.Data.Persistence.Fakers;

public class ProductFaker : Faker<ProductEntity>
{
    public ProductFaker()
    {
        // IndexFaker guarantees natural-key uniqueness at any row count — random-only SKUs
        // birthday-collide in volume seeding, and identical natural keys derive identical
        // deterministic ids (the TDM identity contract). Still deterministic under a seed.
        RuleFor(p => p.Sku, f => $"SKU-{f.IndexFaker:D7}-{f.Random.Replace("??").ToUpperInvariant()}");
        RuleFor(p => p.Name, f => f.Commerce.ProductName());
        RuleFor(p => p.Price, f => decimal.Parse(f.Commerce.Price()));
        RuleFor(p => p.Category, f => f.Commerce.Categories(1)[0]);
        RuleFor(p => p.Discontinued, f => f.Random.Bool(0.1f));
    }
}
