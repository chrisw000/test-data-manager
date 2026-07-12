using System;
using Tdm.Identity;
using Xunit;

namespace Tdm.Core.Tests.Identity;

public class TdmIdentityTests
{
    // ── Determinism ──────────────────────────────────────────────────────────

    [Fact]
    public void ForNaturalKey_SameInputs_ReturnsSameGuid()
    {
        var a = TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd");
        var b = TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ForNaturalKey_DifferentDomain_ReturnsDifferentGuid()
    {
        var a = TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd");
        var b = TdmIdentity.ForNaturalKey("ERP", "Customer", "Acme Ltd");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ForNaturalKey_DifferentEntity_ReturnsDifferentGuid()
    {
        var a = TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd");
        var b = TdmIdentity.ForNaturalKey("CRM", "Account", "Acme Ltd");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ForNaturalKey_DifferentKey_ReturnsDifferentGuid()
    {
        var a = TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd");
        var b = TdmIdentity.ForNaturalKey("CRM", "Customer", "Beta Corp");
        Assert.NotEqual(a, b);
    }

    // ── RFC 4122 structural checks ────────────────────────────────────────────

    [Fact]
    public void ForNaturalKey_VersionNibble_IsVersion5()
    {
        var guid = TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd");
        // In the canonical 8-4-4-4-12 form the version nibble is the first hex
        // digit of the third group (string position 14).
        Assert.Equal('5', guid.ToString("D")[14]);
    }

    [Fact]
    public void ForNaturalKey_VariantBits_AreRfc4122()
    {
        var guid = TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd");
        // In .NET Guid layout byte[8] contains the variant bits; RFC 4122 variant = 10xx xxxx
        var bytes = guid.ToByteArray();
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    // ── ForOrdinal ────────────────────────────────────────────────────────────

    [Fact]
    public void ForOrdinal_SameInputs_ReturnsSameGuid()
    {
        var a = TdmIdentity.ForOrdinal("Orders", "Product", "Bulk Seed", 42, 1);
        var b = TdmIdentity.ForOrdinal("Orders", "Product", "Bulk Seed", 42, 1);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ForOrdinal_DifferentOrdinal_ReturnsDifferentGuid()
    {
        var a = TdmIdentity.ForOrdinal("Orders", "Product", "Bulk Seed", 42, 1);
        var b = TdmIdentity.ForOrdinal("Orders", "Product", "Bulk Seed", 42, 2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ForOrdinal_DifferentSeed_ReturnsDifferentGuid()
    {
        var a = TdmIdentity.ForOrdinal("Orders", "Product", "Bulk Seed", 42, 1);
        var b = TdmIdentity.ForOrdinal("Orders", "Product", "Bulk Seed", 99, 1);
        Assert.NotEqual(a, b);
    }

    // ── FromName ──────────────────────────────────────────────────────────────

    [Fact]
    public void FromName_SameInput_ReturnsSameGuid()
    {
        var a = TdmIdentity.FromName("CRM|Customer|Acme Ltd");
        var b = TdmIdentity.FromName("CRM|Customer|Acme Ltd");
        Assert.Equal(a, b);
    }

    [Fact]
    public void FromName_DifferentInput_ReturnsDifferentGuid()
    {
        var a = TdmIdentity.FromName("CRM|Customer|Acme Ltd");
        var b = TdmIdentity.FromName("CRM|Customer|Beta Corp");
        Assert.NotEqual(a, b);
    }

    // ── Frozen contract vector ────────────────────────────────────────────────
    // This value was computed by running the production code once; it MUST never change.

    [Fact]
    public void ForNaturalKey_ContractVector_FrozenValue()
    {
        var id = TdmIdentity.ForNaturalKey("CRM", "Customer", "Acme Ltd");
        // Verified against an independent RFC 4122 UUIDv5 implementation
        // (SHA-1 over big-endian namespace bytes + UTF-8 name).
        // Any change here is a breaking contract change requiring a version bump.
        Assert.Equal("f629ad79-bbba-5d12-ae83-b8a8b9bf4ce0", id.ToString("D"));
    }

    // ── Null argument guards ──────────────────────────────────────────────────

    [Fact]
    public void ForNaturalKey_NullDomain_Throws() =>
        Assert.Throws<ArgumentNullException>(() => TdmIdentity.ForNaturalKey(null!, "E", "K"));

    [Fact]
    public void ForNaturalKey_NullEntity_Throws() =>
        Assert.Throws<ArgumentNullException>(() => TdmIdentity.ForNaturalKey("D", null!, "K"));

    [Fact]
    public void ForNaturalKey_NullKey_Throws() =>
        Assert.Throws<ArgumentNullException>(() => TdmIdentity.ForNaturalKey("D", "E", null!));

    [Fact]
    public void FromName_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => TdmIdentity.FromName(null!));
}
