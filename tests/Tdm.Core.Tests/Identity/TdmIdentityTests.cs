using AwesomeAssertions;
using Tdm.Identity;
using Xunit;

namespace Tdm.Core.Tests.Identity;

public class TdmIdentityTests
{
    // ── Determinism ──────────────────────────────────────────────────────────

    [Fact]
    public void ForNaturalKey_SameInputs_ReturnsSameGuid() =>
        TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd")
            .Should().Be(TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd"));

    [Fact]
    public void ForNaturalKey_DifferentDomain_ReturnsDifferentGuid() =>
        TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd")
            .Should().NotBe(TdmIdentity.ForNaturalKey("ERP", "Customer", "Acme Ltd"));

    [Fact]
    public void ForNaturalKey_DifferentEntity_ReturnsDifferentGuid() =>
        TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd")
            .Should().NotBe(TdmIdentity.ForNaturalKey("CRM", "Account", "Acme Ltd"));

    [Fact]
    public void ForNaturalKey_DifferentKey_ReturnsDifferentGuid() =>
        TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd")
            .Should().NotBe(TdmIdentity.ForNaturalKey("CRM", "Customer", "Beta Corp"));

    // ── RFC 4122 structural checks ────────────────────────────────────────────

    [Fact]
    public void ForNaturalKey_VersionNibble_IsVersion5()
    {
        var guid = TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd");
        // In the canonical 8-4-4-4-12 form the version nibble is the first hex
        // digit of the third group (string position 14).
        guid.ToString("D")[14].Should().Be('5');
    }

    [Fact]
    public void ForNaturalKey_VariantBits_AreRfc4122()
    {
        var guid = TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd");
        // In .NET Guid layout byte[8] contains the variant bits; RFC 4122 variant = 10xx xxxx.
        (guid.ToByteArray()[8] & 0xC0).Should().Be(0x80);
    }

    // ── ForOrdinal ────────────────────────────────────────────────────────────

    [Fact]
    public void ForOrdinal_SameInputs_ReturnsSameGuid() =>
        TdmIdentity.ForOrdinal("Orders", "Product", "Bulk Seed", 42, 1)
            .Should().Be(TdmIdentity.ForOrdinal("Orders", "Product", "Bulk Seed", 42, 1));

    [Fact]
    public void ForOrdinal_DifferentOrdinal_ReturnsDifferentGuid() =>
        TdmIdentity.ForOrdinal("Orders", "Product", "Bulk Seed", 42, 1)
            .Should().NotBe(TdmIdentity.ForOrdinal("Orders", "Product", "Bulk Seed", 42, 2));

    [Fact]
    public void ForOrdinal_DifferentSeed_ReturnsDifferentGuid() =>
        TdmIdentity.ForOrdinal("Orders", "Product", "Bulk Seed", 42, 1)
            .Should().NotBe(TdmIdentity.ForOrdinal("Orders", "Product", "Bulk Seed", 99, 1));

    // ── FromName ──────────────────────────────────────────────────────────────

    [Fact]
    public void FromName_SameInput_ReturnsSameGuid() =>
        TdmIdentity.FromName("CRM|Customer|Acme Ltd")
            .Should().Be(TdmIdentity.FromName("CRM|Customer|Acme Ltd"));

    [Fact]
    public void FromName_DifferentInput_ReturnsDifferentGuid() =>
        TdmIdentity.FromName("CRM|Customer|Acme Ltd")
            .Should().NotBe(TdmIdentity.FromName("CRM|Customer|Beta Corp"));

    // ── Frozen contract vector ────────────────────────────────────────────────

    [Fact]
    public void ForNaturalKey_ContractVector_FrozenValue()
    {
        var id = TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd");
        // Verified against an independent RFC 4122 UUIDv5 implementation
        // (SHA-1 over big-endian namespace bytes + UTF-8 name).
        // Any change here is a breaking contract change requiring a version bump.
        id.ToString("D").Should().Be("f629ad79-bbba-5d12-ae83-b8a8b9bf4ce0");
    }

    // ── Null argument guards ──────────────────────────────────────────────────

    [Fact]
    public void ForNaturalKey_NullDomain_Throws() =>
        FluentActions.Invoking(() => TdmIdentity.ForNaturalKey(null!, "E", "K"))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void ForNaturalKey_NullEntity_Throws() =>
        FluentActions.Invoking(() => TdmIdentity.ForNaturalKey("D", null!, "K"))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void ForNaturalKey_NullKey_Throws() =>
        FluentActions.Invoking(() => TdmIdentity.ForNaturalKey("D", "E", null!))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void FromName_Null_Throws() =>
        FluentActions.Invoking(() => TdmIdentity.FromName(null!))
            .Should().Throw<ArgumentNullException>();
}
