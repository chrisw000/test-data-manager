using Tdm.Core.Model;

namespace Tdm.Lsp.Tests;

/// <summary>The sample-domains shape in miniature: Orders (Customer, Order) + Billing (Invoice).</summary>
public static class TestModels
{
    public static TdmModel OrdersAndBilling() => new()
    {
        TdmVersion = "0.1.0",
        SettingsFileSha256 = "abc123",
        Domains =
        [
            new TdmModelDomain
            {
                Name = "Orders",
                Entities =
                [
                    new TdmModelEntity
                    {
                        Name = "Customer", ClrType = "Acme.Orders.CustomerEntity", NaturalKey = "Name",
                        Key = "Id: Guid (client-set)", FakerSource = "CustomerFaker",
                        Properties =
                        [
                            new TdmModelProperty { Name = "Name", Type = "string" },
                            new TdmModelProperty { Name = "Tier", Type = "string" },
                            new TdmModelProperty { Name = "CreditLimit", Type = "decimal" },
                        ],
                    },
                    new TdmModelEntity
                    {
                        Name = "Order", ClrType = "Acme.Orders.OrderEntity", NaturalKey = "OrderNumber",
                        Properties =
                        [
                            new TdmModelProperty { Name = "OrderNumber", Type = "string" },
                            new TdmModelProperty { Name = "Status", Type = "string" },
                        ],
                    },
                ],
            },
            new TdmModelDomain
            {
                Name = "Billing",
                Entities =
                [
                    new TdmModelEntity
                    {
                        Name = "Invoice", ClrType = "Acme.Billing.InvoiceModel", NaturalKey = "InvoiceNumber",
                        Properties =
                        [
                            new TdmModelProperty { Name = "InvoiceNumber", Type = "string" },
                            new TdmModelProperty { Name = "Amount", Type = "decimal" },
                            new TdmModelProperty { Name = "Status", Type = "string" },
                        ],
                    },
                ],
            },
        ],
    };
}
