using Acme.Orders.Data.Persistence;
using Acme.Orders.Domain.Catalog;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tdm.Core.Settings;
using Tdm.EfCore.Bulk;
using Tdm.EfCore.Providers;
using Tdm.Providers.PostgreSql;
using Xunit;

namespace Tdm.EfCore.Tests;

/// <summary>
/// The provider plugin seam (W3-D5): registry resolution, plugin discovery, and the Npgsql
/// binary COPY column mapping proven offline against a PostgreSQL-optioned EF model — the
/// same offline-model property `tdm validate` relies on; the live path runs under the
/// container matrix (TDM_TEST_PROVIDER=PostgreSql).
/// </summary>
public class ProviderBootstrapTests
{
    [Theory]
    [InlineData("Sqlite", "Microsoft.EntityFrameworkCore.Sqlite")]
    [InlineData("sqlserver", "Microsoft.EntityFrameworkCore.SqlServer")]
    public void Registry_ResolvesInBoxProviders_CaseInsensitively(string name, string efProviderName)
    {
        var bootstrap = ProviderRegistry.Resolve(name, domainName: "Orders");
        bootstrap.EfProviderName.Should().Be(efProviderName);
        bootstrap.BulkInserter.Should().NotBeNull("both in-box providers ship a native inserter");
    }

    [Fact]
    public void Registry_UnknownProvider_NamesTheRegisteredOnes()
    {
        var resolve = () => ProviderRegistry.Resolve("Cosmos", domainName: "Orders");
        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*Domain 'Orders'*unknown provider 'Cosmos'*Sqlite*SqlServer*IProviderBootstrap*");
    }

    [Fact]
    public void Registry_DiscoversPostgreSqlBootstrap_FromItsAssembly()
    {
        ProviderRegistry.DiscoverFrom([typeof(PostgreSqlProviderBootstrap).Assembly])
            .Should().BeGreaterThanOrEqualTo(1);

        ProviderRegistry.TryResolve("PostgreSql", out var bootstrap).Should().BeTrue();
        bootstrap.EfProviderName.Should().Be("Npgsql.EntityFrameworkCore.PostgreSQL");
        bootstrap.BulkInserter.Should().BeSameAs(PostgreSqlBinaryCopyInserter.Instance);
    }

    [Fact]
    public void ApplyProvider_PostgreSql_ConfiguresNpgsql_AndModelBuildsOffline()
    {
        ProviderRegistry.Register(new PostgreSqlProviderBootstrap());
        var domain = new DomainSettings
        {
            Name = "Orders", Provider = "PostgreSql",
            ConnectionString = "Host=offline;Database=none;Username=x;Password=x",
        };

        // Model building is offline — no connection is opened.
        var options = (DbContextOptions<OrdersDbContext>)DbContextActivator.BuildOptions(typeof(OrdersDbContext), domain);
        using var ctx = new OrdersDbContext(options);
        ctx.Database.ProviderName.Should().Be("Npgsql.EntityFrameworkCore.PostgreSQL");
        ctx.Model.FindEntityType(typeof(ProductEntity)).Should().NotBeNull();
    }

    // ------------------------------------------------- binary COPY mapping, offline

    private static Microsoft.EntityFrameworkCore.Metadata.IEntityType NpgsqlEntityType<T>()
    {
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql("Host=offline;Database=none;Username=x;Password=x")
            .Options;
        using var ctx = new OrdersDbContext(options);
        return ctx.Model.FindEntityType(typeof(T))!;
    }

    [Fact]
    public void NpgsqlModel_MapsInsertableColumns_WithStoreTypes()
    {
        var entityType = NpgsqlEntityType<ProductEntity>();
        BulkColumns.TryMap(entityType, out var map, out var reason).Should().BeTrue(reason);

        map!.Columns.Select(c => c.Property.Name).Should()
            .BeEquivalentTo(["Id", "Sku", "Name", "Price", "Category", "Discontinued"]);
        map.Columns.Should().OnlyContain(c => !string.IsNullOrEmpty(c.StoreType),
            "binary COPY needs every column's declared store type");

        var id = map.Columns.Single(c => c.Property.Name == "Id");
        PostgreSqlBinaryCopyInserter.DataTypeName(id.StoreType).Should().Be("uuid");
        var price = map.Columns.Single(c => c.Property.Name == "Price");
        PostgreSqlBinaryCopyInserter.DataTypeName(price.StoreType).Should().Be("numeric");
    }

    [Theory]
    [InlineData("uuid", "uuid")]
    [InlineData("numeric(18,2)", "numeric")]
    [InlineData("character varying(64)", "character varying")]
    [InlineData("timestamp(6) with time zone", "timestamp with time zone")]
    public void DataTypeName_StripsDeclarationFacets(string storeType, string expected) =>
        PostgreSqlBinaryCopyInserter.DataTypeName(storeType).Should().Be(expected);
}
