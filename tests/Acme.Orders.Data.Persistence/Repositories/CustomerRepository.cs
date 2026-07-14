using Acme.Orders.Data.Persistence.Domain;
using Microsoft.EntityFrameworkCore;

namespace Acme.Orders.Data.Persistence.Repositories;

/// <summary>
/// The modern split-repository shape (ADR-0001): one write and one read repository per
/// entity, deliberately not identified by any generic marker interface — TDM discovers
/// them via the profile's I{Name}WriteRepository / I{Name}ReadRepository probe patterns.
/// </summary>
public interface ICustomerWriteRepository
{
    void Add(CustomerEntity entity);
    void Update(CustomerEntity entity);
    void Delete(CustomerEntity entity);
}

public interface ICustomerReadRepository
{
    Task<CustomerEntity?> GetByName(string name);
}

/// <summary>
/// Carries the domain behaviour worth exercising during seeding — here a simple audit
/// stamp; a real domain would validate, raise events, etc. This is why TDM writes through
/// the write repository rather than the DbContext.
/// </summary>
public class CustomerWriteRepository(OrdersDbContext context) : ICustomerWriteRepository
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

public class CustomerReadRepository(OrdersDbContext context) : ICustomerReadRepository
{
    public Task<CustomerEntity?> GetByName(string name) =>
        context.Customers.SingleOrDefaultAsync(c => c.Name == name);
}
