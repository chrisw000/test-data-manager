using AwesomeAssertions;
using Tdm.Core.Registry;
using Xunit;

namespace Tdm.Core.Tests.Registry;

public class KeyRegistryDocumentTests
{
    [Fact]
    public void TryLoad_MissingFile_ReturnsNull()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tdm-keyreg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try { KeyRegistryDocument.TryLoad(directory).Should().BeNull(); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void TryLoad_ParsesFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tdm-keyreg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, KeyRegistryDocument.FileName), """
            {
              "registryVersion": 1,
              "domain": "Orders",
              "entities": {
                "Customer": { "naturalKey": "Name", "keys": ["Acme Ltd", "Globex Corp"] },
                "Product":  { "naturalKey": "Sku", "keys": [], "keyPattern": "^SKU-\\d{4}-[A-Z]{2}$" }
              }
            }
            """);
        try
        {
            var registry = KeyRegistryDocument.TryLoad(directory);
            registry.Should().NotBeNull();
            registry!.Domain.Should().Be("Orders");
            registry.Entities.Should().ContainKey("Customer").WhoseValue.Keys.Should().Equal("Acme Ltd", "Globex Corp");
            registry.Entities["Product"].KeyPattern.Should().Be(@"^SKU-\d{4}-[A-Z]{2}$");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void IsKeyKnown_ExactMatch_True()
    {
        var registry = new KeyRegistryDocument
        {
            Entities = { ["Customer"] = new EntityKeyRegistry { Keys = ["Acme Ltd"] } },
        };
        registry.IsKeyKnown("Customer", "Acme Ltd").Should().BeTrue();
        registry.IsKeyKnown("Customer", "Nope").Should().BeFalse();
    }

    [Fact]
    public void IsKeyKnown_PatternMatch_True()
    {
        var registry = new KeyRegistryDocument
        {
            Entities = { ["Product"] = new EntityKeyRegistry { KeyPattern = @"^SKU-\d{4}$" } },
        };
        registry.IsKeyKnown("Product", "SKU-1234").Should().BeTrue();
        registry.IsKeyKnown("Product", "SKU-X").Should().BeFalse();
    }

    [Fact]
    public void IsKeyKnown_EntityNotGoverned_True() =>
        new KeyRegistryDocument().IsKeyKnown("Anything", "Whatever").Should().BeTrue();
}
