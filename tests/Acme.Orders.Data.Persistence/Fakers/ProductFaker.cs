using Acme.Orders.Data.Persistence.Domain;
using Bogus;

namespace Acme.Orders.Data.Persistence.Fakers;

public class ProductFaker : Faker<ProductEntity>
{
    public ProductFaker()
    {
        RuleFor(p => p.Sku, f => f.Random.Replace("SKU-####-??").ToUpperInvariant());
        RuleFor(p => p.Name, f => f.Commerce.ProductName());
        RuleFor(p => p.Price, f => decimal.Parse(f.Commerce.Price()));
        RuleFor(p => p.Category, f => f.Commerce.Categories(1)[0]);
        RuleFor(p => p.Discontinued, f => f.Random.Bool(0.1f));
    }
}
