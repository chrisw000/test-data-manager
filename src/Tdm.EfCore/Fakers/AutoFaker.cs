using System.Reflection;
using Bogus;
using Tdm.Core.Execution;

namespace Tdm.EfCore.Fakers;

/// <summary>
/// Heuristic fallback generator used when no convention <c>{Name}Faker</c> exists (handoff §5.3):
/// property-name heuristics first, then property-type rules. Navigation properties, foreign key
/// columns and the primary key are skipped — relationships are explicit via references, and keys
/// come from the identity contract.
/// </summary>
internal static class AutoFaker
{
    public static object Generate(EntityDescriptor descriptor, IReadOnlySet<string> skipProperties, Faker faker)
    {
        var instance = Activator.CreateInstance(descriptor.ClrType)
            ?? throw new InvalidOperationException($"{descriptor.ClrType.Name} has no public parameterless constructor.");

        foreach (var prop in descriptor.ClrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite || skipProperties.Contains(prop.Name)) continue;
            var value = ValueFor(prop, faker);
            if (value is not null) prop.SetValue(instance, value);
        }
        return instance;
    }

    private static object? ValueFor(PropertyInfo prop, Faker f)
    {
        var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        var name = prop.Name.ToLowerInvariant();

        // Name heuristics (checked before type rules).
        if (type == typeof(string))
        {
            if (name.Contains("email")) return f.Internet.Email();
            if (name.Contains("firstname")) return f.Name.FirstName();
            if (name.Contains("lastname") || name.Contains("surname")) return f.Name.LastName();
            if (name.Contains("company") || name == "name") return f.Company.CompanyName();
            if (name.Contains("fullname") || name.EndsWith("name")) return f.Name.FullName();
            if (name.Contains("phone")) return f.Phone.PhoneNumber();
            if (name.Contains("street") || name.Contains("address")) return f.Address.StreetAddress();
            if (name.Contains("city")) return f.Address.City();
            if (name.Contains("postcode") || name.Contains("zip")) return f.Address.ZipCode();
            if (name.Contains("country")) return f.Address.Country();
            if (name.Contains("sku")) return f.Random.Replace("SKU-####-??").ToUpperInvariant();
            if (name.Contains("url") || name.Contains("website")) return f.Internet.Url();
            if (name.Contains("description") || name.Contains("notes") || name.Contains("comment")) return f.Lorem.Sentence();
            if (name.Contains("currency")) return f.Finance.Currency().Code;
            if (name.Contains("iban")) return f.Finance.Iban();
            if (name.Contains("code") || name.Contains("reference") || name.Contains("number"))
                return f.Random.AlphaNumeric(10).ToUpperInvariant();
            return f.Lorem.Word();
        }

        if (type == typeof(decimal))
        {
            if (name.Contains("price") || name.Contains("amount") || name.Contains("total") ||
                name.Contains("cost") || name.Contains("limit") || name.Contains("balance"))
                return f.Finance.Amount();
            return f.Finance.Amount(0, 10_000);
        }

        if (type.IsEnum) return f.Random.ArrayElement(Enum.GetValues(type).Cast<object>().ToArray());

        if (type == typeof(int)) return f.Random.Int(1, 1_000);
        if (type == typeof(long)) return f.Random.Long(1, 1_000_000);
        if (type == typeof(short)) return f.Random.Short(1, 1_000);
        if (type == typeof(byte)) return f.Random.Byte();
        if (type == typeof(double)) return Math.Round(f.Random.Double(0, 10_000), 2);
        if (type == typeof(float)) return (float)Math.Round(f.Random.Float(0, 10_000), 2);
        if (type == typeof(bool)) return f.Random.Bool();
        // Seed-deterministic (Randomizer-derived), unlike Guid.NewGuid().
        if (type == typeof(Guid)) return f.Random.Guid();
        // Recent past per handoff §5.3. The offset is seed-deterministic; the anchor is 'now'
        // by design — evergreen data. The manifest records the final value for exact playback.
        if (type == typeof(DateTime)) return f.Date.Recent(30).ToUniversalTime();
        if (type == typeof(DateTimeOffset)) return f.Date.RecentOffset(30);
        if (type == typeof(DateOnly)) return DateOnly.FromDateTime(f.Date.Recent(30));
        if (type == typeof(TimeOnly)) return TimeOnly.FromDateTime(f.Date.Recent(1));
        if (type == typeof(TimeSpan)) return TimeSpan.FromMinutes(f.Random.Int(1, 24 * 60));

        return null; // unknown/complex type — left at default, relationships come via references
    }
}
