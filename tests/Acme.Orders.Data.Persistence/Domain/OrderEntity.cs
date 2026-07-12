namespace Acme.Orders.Data.Persistence.Domain;

public enum OrderStatus { Draft, Pending, Shipped, Delivered, Cancelled }

public class OrderEntity
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = "";
    public Guid CustomerId { get; set; }
    public CustomerEntity? Customer { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime OrderDate { get; set; }
    public decimal Total { get; set; }
}
