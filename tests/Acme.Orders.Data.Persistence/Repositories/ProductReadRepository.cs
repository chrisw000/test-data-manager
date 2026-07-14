using Acme.Orders.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Acme.Orders.Data.Persistence.Repositories;

/// <summary>
/// Product deliberately has a read repository but no write repository: it is exempted from
/// the write-repository policy in tdm.settings.json (entities.Product.requireRepository: false),
/// so TDM persists it via the DbContext and reports the exemption instead of failing validate.
/// </summary>
public interface IProductReadRepository
{
    Task<ProductEntity?> GetBySku(string sku);
}

public class ProductReadRepository(OrdersDbContext context) : IProductReadRepository
{
    public Task<ProductEntity?> GetBySku(string sku) =>
        context.Products.SingleOrDefaultAsync(p => p.Sku == sku);
}
