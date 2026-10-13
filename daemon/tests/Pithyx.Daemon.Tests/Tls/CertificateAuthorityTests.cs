using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Tls;

namespace Pithyx.Daemon.Tests.Tls;

public sealed class CertificateAuthorityTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    private static NameScope Scope() => NameScope.Local("pithyx", ["fritz.box"], ["home.example.org"]);

    [Theory]
    [InlineData("pithyx", true)]
    [InlineData("pithyx.local", true)]
    [InlineData("nas.lan", true)]
    [InlineData("printer.home.arpa", true)]
    [InlineData("pithyx.fritz.box", true)]
    [InlineData("media.home.example.org", true)]
    [InlineData("*.home.example.org", true)]
    [InlineData("localhost", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("::1", true)]
    [InlineData("example.org", false)]
    [InlineData("bank.example", false)]
    [InlineData("notpithyx", false)]
    [InlineData("box", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    public void ARestrictedScopePermitsOnlyLocalNamesAndPrivateAddresses(string name, bool permitted) =>
        Assert.Equal(permitted, Scope().Permits(name));

    [Fact]
    public void DropsSuffixesAnotherSuffixCoversAndInvalidSearchDomains()
    {
        var scope = NameScope.Local("Pithyx", ["office.lan", "Fritz.Box", "bad_domain"], []);

        Assert.Equal(["local", "lan", "home.arpa", "internal", "localhost", "pithyx", "fritz.box"], scope.Domains);
    }

    [Fact]
    public void TheConstraintsSurviveTheRoundTripThroughTheRoot()
    {
        var root = CertificateAuthority.CreateRoot("pithyx", Scope(), _now);

        var read = NameScope.FromCertificate(root.Certificate);

        Assert.True(read.Restricted);
        Assert.Equal(Scope().Domains, read.Domains);
        Assert.Equal(NameScope.PrivateAddresses.Select(range => range.ToString()), read.Addresses.Select(range => range.ToString()));
        Assert.True(root.Certificate.Extensions[NameScope.ExtensionOid]!.Critical);
    }

    [Fact]
    public void AnUnrestrictedRootHasNoConstraints()
    {
        var root = CertificateAuthority.CreateRoot("pithyx", NameScope.Unrestricted, _now);

        Assert.Null(root.Certificate.Extensions[NameScope.ExtensionOid]);
        Assert.False(NameScope.FromCertificate(root.Certificate).Restricted);
    }

    [Theory]
    [InlineData(" NAS.Lan ", "nas.lan")]
    [InlineData("*.media.lan", "*.media.lan")]
    [InlineData("192.168.001.010", null)]
    [InlineData("192.168.1.10", "192.168.1.10")]
    [InlineData("FD00::0001", "fd00::1")]
    [InlineData("*.lan", null)]
    [InlineData("nas..lan", null)]
    [InlineData("-nas.lan", null)]
    [InlineData("nas_1.lan", null)]
    [InlineData("", null)]
    public void ChecksNames(string name, string? expected)
    {
        if (expected is null)
        {
            Assert.Equal(ErrorCodes.TlsNameInvalid, Assert.Throws<PithyxException>(() => NameScope.Name(name)).Code);
        }
        else
        {
            Assert.Equal(expected, NameScope.Name(name));
        }
    }

    [Fact]
    public void ADeviceTrustingTheRootAcceptsTheChain()
    {
        var root = CertificateAuthority.CreateRoot("pithyx", Scope(), _now);
        var intermediate = CertificateAuthority.CreateIntermediate(root, "pithyx", _now);
        var issued = CertificateAuthority.Issue(intermediate, ["pithyx.local", "192.168.1.20", "::1"], CertificateAuthority.ServerDays, CertificateKeyType.Ecdsa, _now);

        using var chain = Chain(root, intermediate);
        Assert.True(chain.Build(issued.Certificate), string.Join(", ", chain.ChainStatus.Select(status => status.StatusInformation)));
        Assert.Equal(3, chain.ChainElements.Count);
        Assert.Equal(["pithyx.local", "192.168.1.20", "::1"], CertificateAuthority.Names(issued.Certificate));
        Assert.True(CertificateAuthority.IsIssuedBy(issued.Certificate, intermediate.Certificate));
        Assert.True(CertificateAuthority.IsIssuedBy(intermediate.Certificate, root.Certificate));
        Assert.False(CertificateAuthority.IsIssuedBy(issued.Certificate, root.Certificate));
        Assert.Equal(_now.AddDays(CertificateAuthority.ServerDays).UtcDateTime, issued.Certificate.NotAfter.ToUniversalTime());
    }

    [Fact]
    public void ADeviceRefusesANameOutsideTheScope()
    {
        var root = CertificateAuthority.CreateRoot("pithyx", Scope(), _now);
        var intermediate = CertificateAuthority.CreateIntermediate(root, "pithyx", _now);
        var issued = CertificateAuthority.Issue(intermediate, ["bank.example"], 30, CertificateKeyType.Ecdsa, _now);

        using var chain = Chain(root, intermediate);
        Assert.False(chain.Build(issued.Certificate));
        Assert.Contains(chain.ChainStatus, status => status.Status.HasFlag(X509ChainStatusFlags.HasNotPermittedNameConstraint));
    }

    [Fact]
    public void TheRootsConstraintsHoldEvenForAnUnrestrictedIntermediateItsKeySigned()
    {
        // A compromised box could sign a new intermediate without constraints; the root's own still apply.
        var root = CertificateAuthority.CreateRoot("pithyx", Scope(), _now);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Rogue", key, HashAlgorithmName.SHA384);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var rogue = new SigningCertificate(
            request.Create(root.Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(root.Key), _now.AddHours(-1), _now.AddYears(1), [1, 2, 3]),
            key);
        var issued = CertificateAuthority.Issue(rogue, ["bank.example"], 30, CertificateKeyType.Ecdsa, _now);

        using var chain = Chain(root, rogue);
        Assert.False(chain.Build(issued.Certificate));
        Assert.Contains(chain.ChainStatus, status => status.Status.HasFlag(X509ChainStatusFlags.HasNotPermittedNameConstraint));
    }

    [Fact]
    public void IssuesRsaCertificatesForDevicesThatNeedThem()
    {
        var root = CertificateAuthority.CreateRoot("pithyx", Scope(), _now);
        var intermediate = CertificateAuthority.CreateIntermediate(root, "pithyx", _now);

        var issued = CertificateAuthority.Issue(intermediate, ["printer.lan"], 825, CertificateKeyType.Rsa, _now);

        using var rsa = issued.Certificate.GetRSAPublicKey();
        Assert.Equal(2048, rsa!.KeySize);
        Assert.Contains("BEGIN PRIVATE KEY", issued.PrivateKeyPem, StringComparison.Ordinal);
        using var chain = Chain(root, intermediate);
        Assert.True(chain.Build(issued.Certificate));
    }

    [Fact]
    public void NoCertificateOutlivesItsIssuer()
    {
        var root = CertificateAuthority.CreateRoot("pithyx", Scope(), _now);
        var intermediate = CertificateAuthority.CreateIntermediate(root, "pithyx", _now.AddYears(19));

        var issued = CertificateAuthority.Issue(intermediate, ["nas.lan"], 825, CertificateKeyType.Ecdsa, _now.AddYears(19).AddDays(200));

        Assert.Equal(root.Certificate.NotAfter, intermediate.Certificate.NotAfter);
        Assert.Equal(intermediate.Certificate.NotAfter, issued.Certificate.NotAfter);
    }

    [Fact]
    public void ReadsTheSearchDomainsOfResolvConf()
    {
        var domains = BoxCertificates.ParseSearchDomains("# managed\nnameserver 127.0.0.53\nsearch fritz.box Office.LAN.\noptions edns0\ndomain fritz.box\n");

        Assert.Equal(["fritz.box", "office.lan"], domains);
    }

    [Fact]
    public void TheBoxsNamesStayInsideTheScope()
    {
        var names = BoxCertificates.BoxNames(
            "Pithyx",
            ["fritz.box"],
            ["nas.home.example.org"],
            [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("2001:db8::20"), IPAddress.Parse("fd00::20"), IPAddress.Parse("fe80::1%2"), IPAddress.Parse("::ffff:10.0.0.5")],
            Scope());

        Assert.Equal(
            ["pithyx", "pithyx.local", "pithyx.fritz.box", "localhost", "nas.home.example.org", "192.168.1.20", "10.0.0.5", "fd00::20", "127.0.0.1", "::1"],
            names);
    }

    [Fact]
    public void ChecksAnUploadedCertificate()
    {
        var root = CertificateAuthority.CreateRoot("other", NameScope.Unrestricted, _now);
        var intermediate = CertificateAuthority.CreateIntermediate(root, "other", _now);
        var issued = CertificateAuthority.Issue(intermediate, ["nas.example.org"], 90, CertificateKeyType.Rsa, _now);
        var other = CertificateAuthority.Issue(intermediate, ["nas.example.org"], 90, CertificateKeyType.Ecdsa, _now);
        var chain = issued.CertificatePem + CertificateAuthority.Pem(intermediate.Certificate);

        var parsed = BoxCertificates.ParseCustom(chain, issued.PrivateKeyPem, _now);
        Assert.Equal(2, parsed.Count);
        Assert.True(parsed[0].HasPrivateKey);

        Assert.Equal(ErrorCodes.TlsCustomKeyMismatch, Assert.Throws<PithyxException>(() => BoxCertificates.ParseCustom(chain, other.PrivateKeyPem, _now)).Code);
        Assert.Equal(ErrorCodes.TlsCustomInvalid, Assert.Throws<PithyxException>(() => BoxCertificates.ParseCustom("not a certificate", issued.PrivateKeyPem, _now)).Code);
        Assert.Equal(ErrorCodes.TlsCustomExpired, Assert.Throws<PithyxException>(() => BoxCertificates.ParseCustom(chain, issued.PrivateKeyPem, _now.AddDays(91))).Code);
    }

    private static X509Chain Chain(SigningCertificate root, SigningCertificate intermediate)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root.Certificate);
        chain.ChainPolicy.ExtraStore.Add(intermediate.Certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = _now.UtcDateTime;
        return chain;
    }
}
