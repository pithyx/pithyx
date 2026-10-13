using System.Formats.Asn1;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Tls;

/// <summary>An address range of a name constraint, such as <c>192.168.0.0/16</c>.</summary>
public sealed record AddressRange(IPAddress Network, int PrefixLength)
{
    public static AddressRange Parse(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var parsed = IPNetwork.Parse(text);
        return new AddressRange(parsed.BaseAddress, parsed.PrefixLength);
    }

    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        return address.AddressFamily == Network.AddressFamily && new IPNetwork(Network, PrefixLength).Contains(address);
    }

    /// <summary>The address and its mask, as the <c>iPAddress</c> form of a general subtree carries them (RFC 5280, 4.2.1.10).</summary>
    public byte[] ToSubtreeBytes()
    {
        var address = Network.GetAddressBytes();
        var mask = new byte[address.Length];
        for (var bit = 0; bit < PrefixLength; bit++)
        {
            mask[bit / 8] |= (byte)(0x80 >> (bit % 8));
        }
        return [.. address, .. mask];
    }

    public static AddressRange FromSubtreeBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is not (8 or 32))
        {
            throw new FormatException("An iPAddress subtree has 8 or 32 bytes.");
        }
        var half = bytes.Length / 2;
        var prefix = 0;
        foreach (var part in bytes[half..])
        {
            prefix += BitOperations.PopCount(part);
        }
        return new AddressRange(new IPAddress(bytes[..half]), prefix);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Network}/{PrefixLength}");
}

/// <summary>
/// What the box's certificate authority may sign (D346): its Name Constraints. A restricted scope permits only the
/// listed DNS suffixes and address ranges, so a device that trusts the authority never accepts a certificate from it
/// for a public site, even if the box is compromised. An unrestricted scope has no constraint at all.
/// </summary>
public sealed partial record NameScope(bool Restricted, IReadOnlyList<string> Domains, IReadOnlyList<AddressRange> Addresses)
{
    public const string ExtensionOid = "2.5.29.30";

    /// <summary>The private, loopback, link-local and shared (RFC 6598, used by mesh VPNs) address ranges.</summary>
    public static IReadOnlyList<AddressRange> PrivateAddresses { get; } =
    [
        .. new[]
        {
            "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "169.254.0.0/16", "127.0.0.0/8",
            "fc00::/7", "fe80::/10", "::1/128",
        }.Select(AddressRange.Parse),
    ];

    /// <summary>Suffixes reserved or commonly used for local networks, and <c>localhost</c>.</summary>
    public static IReadOnlyList<string> LocalDomains { get; } = ["local", "lan", "home.arpa", "internal", "localhost"];

    public static NameScope Unrestricted { get; } = new(false, [], []);

    /// <summary>The default scope: the local suffixes, the host name, the network's search domains and <paramref name="extra"/>.</summary>
    public static NameScope Local(string hostName, IEnumerable<string> searchDomains, IEnumerable<string> extra)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostName);
        ArgumentNullException.ThrowIfNull(searchDomains);
        ArgumentNullException.ThrowIfNull(extra);
        var domains = LocalDomains
            .Append(hostName)
            .Concat(searchDomains.Where(domain => IsDomain(domain.ToLowerInvariant())))
            .Concat(extra.Select(Domain))
            .Select(domain => domain.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            // A suffix covers every name below it, so a name under another listed suffix adds nothing.
            .ToList();
        domains = [.. domains.Where(domain => !domains.Any(other => other != domain && domain.EndsWith("." + other, StringComparison.Ordinal)))];
        return new NameScope(true, domains, PrivateAddresses);
    }

    /// <summary>Whether a certificate from the authority may carry <paramref name="name"/>, a DNS name or an address.</summary>
    public bool Permits(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!Restricted)
        {
            return true;
        }
        if (IPAddress.TryParse(name, out var address))
        {
            return Addresses.Any(range => range.Contains(address));
        }
        var host = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name;
        return Domains.Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
    }

    /// <summary>The Name Constraints extension; critical, as RFC 5280 asks of a certificate authority.</summary>
    public X509Extension? ToExtension()
    {
        if (!Restricted)
        {
            return null;
        }
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
            {
                foreach (var domain in Domains)
                {
                    using (writer.PushSequence())
                    {
                        writer.WriteCharacterString(UniversalTagNumber.IA5String, domain, new Asn1Tag(TagClass.ContextSpecific, 2));
                    }
                }
                foreach (var range in Addresses)
                {
                    using (writer.PushSequence())
                    {
                        writer.WriteOctetString(range.ToSubtreeBytes(), new Asn1Tag(TagClass.ContextSpecific, 7));
                    }
                }
            }
        }
        return new X509Extension(ExtensionOid, writer.Encode(), critical: true);
    }

    /// <summary>The scope a certificate authority's Name Constraints describe; unrestricted without them.</summary>
    public static NameScope FromCertificate(X509Certificate2 authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (authority.Extensions[ExtensionOid] is not { } extension)
        {
            return Unrestricted;
        }
        var domains = new List<string>();
        var addresses = new List<AddressRange>();
        var outer = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
        var permittedTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        if (outer.HasData && outer.PeekTag().HasSameClassAndValue(permittedTag))
        {
            var permitted = outer.ReadSequence(permittedTag);
            while (permitted.HasData)
            {
                var subtree = permitted.ReadSequence();
                var tag = subtree.PeekTag();
                if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                {
                    domains.Add(subtree.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)));
                }
                else if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 7)))
                {
                    addresses.Add(AddressRange.FromSubtreeBytes(subtree.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7))));
                }
            }
        }
        return new NameScope(true, domains, addresses);
    }

    /// <summary>
    /// A name for a certificate: a DNS name (letters, digits, hyphens; a leading <c>*.</c> needs two labels after
    /// it) or an IP address, in lower case. Throws <c>tls.name.invalid</c>.
    /// </summary>
    public static string Name(string? name)
    {
        var trimmed = name?.Trim().ToLowerInvariant() ?? "";
        // IPv4 only in its usual form: the parser also takes "1.2.3" and octal parts such as "010".
        if (IPAddress.TryParse(trimmed, out var address)
            && (address.AddressFamily == AddressFamily.InterNetworkV6 ? trimmed.Contains(':', StringComparison.Ordinal) : address.ToString() == trimmed))
        {
            return address.ToString();
        }
        var host = trimmed.StartsWith("*.", StringComparison.Ordinal) ? trimmed[2..] : trimmed;
        if (IsDomain(host) && !host.Split('.')[^1].All(char.IsAsciiDigit) && (host.Length == trimmed.Length || host.Contains('.', StringComparison.Ordinal)))
        {
            return trimmed;
        }
        throw new PithyxException(ErrorCodes.TlsNameInvalid, new Dictionary<string, object?> { ["name"] = name ?? "" });
    }

    /// <summary>A DNS suffix for the scope: a domain without a wildcard. Throws <c>tls.name.invalid</c>.</summary>
    public static string Domain(string? domain)
    {
        var trimmed = domain?.Trim().TrimStart('.').ToLowerInvariant() ?? "";
        return IsDomain(trimmed)
            ? trimmed
            : throw new PithyxException(ErrorCodes.TlsNameInvalid, new Dictionary<string, object?> { ["name"] = domain ?? "" });
    }

    private static bool IsDomain(string name) =>
        name.Length is > 0 and <= 253 && name.Split('.').All(label => LabelPattern().IsMatch(label));

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();
}
