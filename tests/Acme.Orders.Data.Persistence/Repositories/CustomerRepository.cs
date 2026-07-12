using Acme.Orders.Data.Persistence.Domain;

namespace Acme.Orders.Data.Persistence.Repositories;

/// <summary>The "well-known" repository shape the TDM probes first (handoff §5.2).</summary>
public interface IRepository<T> where T : class
{
    void Add(T entity);
    void Update(T entity);
    void Delete(T entity);
}

public interface ICustomerRepository : IRepository<CustomerEntity>;

/// <summary>
/// Carries the domain behaviour worth exercising during seeding — here a simple audit
/// stamp; a real domain would validate, raise events, etc.
/// </summary>
public class CustomerRepository(OrdersDbContext context) : ICustomerRepository
{
    public void Add(CustomerEntity entity)
    {
        if (entity.CreatedUtc == default) entity.CreatedUtc = DateTime.UtcNow;
        context.Customers.Add(entity);
        context.SaveChanges();
    }

    public void Update(CustomerEntity entity)
    {
        context.Customers.Update(entity);
        context.SaveChanges();
    }

    public void Delete(CustomerEntity entity)
    {
        context.Customers.Remove(entity);
        context.SaveChanges();
    }
}
