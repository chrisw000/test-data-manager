using Acme.Fulfilment.Data.Domain;
using Bogus;

namespace Acme.Fulfilment.Data.Fakers;

public class LocationFaker : Faker<LocationEntity>
{
    public LocationFaker()
    {
        RuleFor(l => l.Name, f => $"{f.Address.City()} {f.Commerce.Department()}");
    }
}
