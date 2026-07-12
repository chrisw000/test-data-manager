using Acme.Orders.Data.Persistence.Domain;
using Bogus;

namespace Acme.Orders.Data.Persistence.Fakers;

/// <summary>
/// Convention faker resolved by the {Name}Faker pattern. No randomisation in the
/// constructor — rules only — so the TDM's post-construction UseSeed applies cleanly.
/// </summary>
public class CustomerFaker : Faker<CustomerEntity>
{
    public CustomerFaker()
    {
        RuleFor(c => c.Name, f => f.Company.CompanyName());
        RuleFor(c => c.Tier, f => f.PickRandom("Standard", "Silver", "Gold", "Platinum"));
        RuleFor(c => c.Email, (f, c) => f.Internet.Email(provider: "example.com"));
        RuleFor(c => c.CreditLimit, f => f.Finance.Amount(1_000, 100_000));
        RuleFor(c => c.CreatedUtc, f => f.Date.Recent(90).ToUniversalTime());
    }
}
