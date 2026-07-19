using Acme.Fulfilment.Data.Domain;

namespace Acme.Fulfilment.Data.Repositories;

/// <summary>Duck-typed "{Name}" methods (AddLocation/…) — exercises the modern profile's
/// repository probe just like the Orders domain.</summary>
public interface ILocationRepository
{
    Task AddLocation(LocationEntity location);
    Task UpdateLocation(LocationEntity location);
    Task DeleteLocation(LocationEntity location);
}

public class LocationRepository(FulfilmentDbContext context) : ILocationRepository
{
    public async Task AddLocation(LocationEntity location)
    {
        context.Locations.Add(location);
        await context.SaveChangesAsync();
    }

    public async Task UpdateLocation(LocationEntity location)
    {
        context.Locations.Update(location);
        await context.SaveChangesAsync();
    }

    public async Task DeleteLocation(LocationEntity location)
    {
        context.Locations.Remove(location);
        await context.SaveChangesAsync();
    }
}
