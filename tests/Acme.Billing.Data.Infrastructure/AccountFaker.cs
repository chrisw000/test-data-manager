using Bogus;

namespace Acme.Billing.Data.Infrastructure;

public class AccountFaker : Faker<AccountModel>
{
    public AccountFaker()
    {
        RuleFor(a => a.Name, f => f.Company.CompanyName());
        RuleFor(a => a.Currency, f => f.PickRandom("GBP", "EUR", "USD"));
        RuleFor(a => a.Balance, f => f.Finance.Amount(-5_000, 50_000));
        RuleFor(a => a.Active, f => f.Random.Bool(0.9f));
    }
}
