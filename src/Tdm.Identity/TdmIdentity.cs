using System;
using System.Security.Cryptography;
using System.Text;

namespace Tdm.Identity;

/// <summary>
/// The TDM Identity Contract (v1).
///
/// Deterministic, name-based (UUIDv5 / RFC 4122 §4.3) identities for synthetic test data.
/// Any party holding this package — the TDM itself, API mocks, other tooling — computes
/// identical GUIDs from the same inputs, enabling cross-domain identity agreement without
/// any runtime coordination ("coordination without communication").
///
/// FROZEN: the namespace GUID and canonical string formats are part of the contract.
/// Any change is breaking and requires a contract version bump.
/// </summary>
public static class TdmIdentity
{
    /// <summary>Contract version. Bumped only on breaking derivation changes.</summary>
    public const string ContractVersion = "1";

    /// <summary>
    /// The fixed TDM namespace GUID all identities are derived under.
    /// Never change this value.
    /// </summary>
    public static readonly Guid Namespace = new("8f1b9c6e-2a4d-5e7f-9b3c-6d8e0f2a4b5c");

    /// <summary>
    /// Identity for an entity with a natural key. Canonical name:
    /// <c>{owningDomain}|{entity}|{naturalKey}</c>, e.g. <c>CRM|Customer|Acme Ltd</c>.
    /// Scenario names, seeds and ordinals deliberately do not participate, so renaming
    /// or reordering scenarios never changes an identity.
    /// </summary>
    public static Guid ForNaturalKey(string owningDomain, string entity, string naturalKey)
    {
        if (owningDomain is null) throw new ArgumentNullException(nameof(owningDomain));
        if (entity is null) throw new ArgumentNullException(nameof(entity));
        if (naturalKey is null) throw new ArgumentNullException(nameof(naturalKey));
        return FromName(owningDomain + "|" + entity + "|" + naturalKey);
    }

    /// <summary>
    /// Identity for a bulk-generated filler entity with no natural key. Canonical name:
    /// <c>{owningDomain}|{entity}|{scenario}|{seed}|{ordinal}</c>. By definition nothing
    /// external references these.
    /// </summary>
    public static Guid ForOrdinal(string owningDomain, string entity, string scenario, int seed, int ordinal)
    {
        if (owningDomain is null) throw new ArgumentNullException(nameof(owningDomain));
        if (entity is null) throw new ArgumentNullException(nameof(entity));
        if (scenario is null) throw new ArgumentNullException(nameof(scenario));
        return FromName(owningDomain + "|" + entity + "|" + scenario + "|" +
                        seed.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
                        ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Raw UUIDv5 derivation under the TDM namespace from an already-canonicalised name.
    /// Prefer <see cref="ForNaturalKey"/> / <see cref="ForOrdinal"/>.
    /// </summary>
    public static Guid FromName(string canonicalName)
    {
        if (canonicalName is null) throw new ArgumentNullException(nameof(canonicalName));

        // RFC 4122 §4.3: SHA-1 over (namespace bytes in network order + name bytes in UTF-8).
        byte[] namespaceBytes = Namespace.ToByteArray();
        SwapByteOrder(namespaceBytes);
        byte[] nameBytes = Encoding.UTF8.GetBytes(canonicalName);

        byte[] hash;
#if NET
        byte[] input = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);
        hash = SHA1.HashData(input);
#else
        using (var sha1 = SHA1.Create())
        {
            sha1.TransformBlock(namespaceBytes, 0, namespaceBytes.Length, null, 0);
            sha1.TransformFinalBlock(nameBytes, 0, nameBytes.Length);
            hash = sha1.Hash;
        }
#endif

        byte[] result = new byte[16];
        Array.Copy(hash, result, 16);
        result[6] = (byte)((result[6] & 0x0F) | 0x50); // version 5
        result[8] = (byte)((result[8] & 0x3F) | 0x80); // RFC 4122 variant
        SwapByteOrder(result); // back to little-endian field layout for Guid ctor
        return new Guid(result);
    }

    // Guid.ToByteArray emits the first three fields little-endian; RFC 4122 hashing
    // requires network (big-endian) order. Swapping is its own inverse.
    private static void SwapByteOrder(byte[] guidBytes)
    {
        Swap(guidBytes, 0, 3);
        Swap(guidBytes, 1, 2);
        Swap(guidBytes, 4, 5);
        Swap(guidBytes, 6, 7);
    }

    private static void Swap(byte[] bytes, int a, int b)
    {
        byte t = bytes[a];
        bytes[a] = bytes[b];
        bytes[b] = t;
    }
}
