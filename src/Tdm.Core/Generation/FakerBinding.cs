using System.Reflection;
using Bogus;
using Tdm.Core.Naming;
using Tdm.Core.Settings;

namespace Tdm.Core.Generation;

/// <summary>
/// Convention faker discovery (handoff §5.3): a type named per the profile's
/// <c>fakerPattern</c> deriving from <c>Faker&lt;TEntity&gt;</c> in the plugin assemblies.
/// Instantiation prefers a parameterless constructor, else one taking <c>int seed</c>.
/// <c>UseSeed</c> is applied after construction — fakers must not randomise in constructors.
/// Shared by the EF and API domain runtimes (W4-D6): one discovery, identical generation.
/// </summary>
public sealed class FakerBinding(Type fakerType)
{
    public Type FakerType { get; } = fakerType;

    public object CreateSeeded(int seed)
    {
        object instance;
        var parameterless = FakerType.GetConstructor(Type.EmptyTypes);
        if (parameterless is not null)
        {
            instance = parameterless.Invoke([]);
        }
        else
        {
            var seedCtor = FakerType.GetConstructor([typeof(int)])
                ?? throw new InvalidOperationException(
                    $"Faker '{FakerType.Name}' needs a parameterless constructor or one taking (int seed).");
            instance = seedCtor.Invoke([seed]);
        }

        var useSeed = FakerType.GetMethod("UseSeed", [typeof(int)])
            ?? throw new InvalidOperationException($"'{FakerType.Name}' does not expose UseSeed(int).");
        useSeed.Invoke(instance, [seed]);
        return instance;
    }

    public object Generate(object fakerInstance)
    {
        var generate = FakerType.GetMethod("Generate", [typeof(string)])
            ?? throw new InvalidOperationException($"'{FakerType.Name}' does not expose Generate(string).");
        return generate.Invoke(fakerInstance, [null])!;
    }

    public static FakerBinding? Find(string logicalName, Type entityType,
        ConventionProfile profile, IReadOnlyList<Assembly> assemblies)
    {
        var wantedName = NameMatcher.Expand(profile.FakerPattern, logicalName);
        var fakerBase = typeof(Faker<>).MakeGenericType(entityType);

        var match = assemblies
            .SelectMany(AssemblyScan.SafeGetTypes)
            .FirstOrDefault(t => t is { IsClass: true, IsAbstract: false } &&
                                 string.Equals(t.Name, wantedName, StringComparison.OrdinalIgnoreCase) &&
                                 fakerBase.IsAssignableFrom(t));
        return match is null ? null : new FakerBinding(match);
    }
}
