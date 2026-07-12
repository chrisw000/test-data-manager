using Acme.Orders.Data.Persistence.Domain;

namespace Acme.Orders.Data.Persistence.Repositories;

/// <summary>Deliberately not IRepository&lt;T&gt;-shaped — exercises the TDM's duck-typed
/// "{Name}"-pattern method matching (AddOrder/UpdateOrder/DeleteOrder).</summary>
public interface IOrderRepository
{
    Task AddOrder(OrderEntity order);
    Task UpdateOrder(OrderEntity order);
    Task DeleteOrder(OrderEntity order);
}

public class OrderRepository(OrdersDbContext context) : IOrderRepository
{
    public async Task AddOrder(OrderEntity order)
    {
        if (string.IsNullOrEmpty(order.OrderNumber))
            order.OrderNumber = $"ORD-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}";
        context.Orders.Add(order);
        await context.SaveChangesAsync();
    }

    public async Task UpdateOrder(OrderEntity order)
    {
        context.Orders.Update(order);
        await context.SaveChangesAsync();
    }

    public async Task DeleteOrder(OrderEntity order)
    {
        context.Orders.Remove(order);
        await context.SaveChangesAsync();
    }
}
