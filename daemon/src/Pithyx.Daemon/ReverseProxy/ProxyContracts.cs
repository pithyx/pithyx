using Pithyx.Daemon.Tls;

namespace Pithyx.Daemon.ReverseProxy;

/// <summary>How the proxy checks the certificate of an HTTPS target.</summary>
public enum ProxyTargetTls
{
    /// <summary>Against the system's trusted authorities.</summary>
    System,

    /// <summary>Against the box's own certificate authority, for devices with a certificate from it.</summary>
    Box,

    /// <summary>Not at all, for a target with a self-signed certificate.</summary>
    None,
}

/// <summary>Which certificate a host presents on 443.</summary>
public enum ProxyCertificateSource
{
    /// <summary>One from the box's certificate authority, made for the host's domains; they must lie in its scope.</summary>
    Box,

    /// <summary>One an administrator uploaded for the host.</summary>
    Custom,

    /// <summary>One from an ACME authority (Let's Encrypt), requested and renewed by the box over HTTP-01 on port 80 (D378).</summary>
    Acme,
}

/// <summary>What 443 and 80 do with a name no enabled host has.</summary>
public enum UnknownHostAction
{
    /// <summary>Redirect to the web interface on 7484, as without a reverse proxy.</summary>
    Redirect,

    /// <summary>Answer 404.</summary>
    NotFound,
}

/// <summary>A header the proxy sets; an empty value removes it.</summary>
/// <param name="Name">The header's name, letters, digits and <c>-</c>; never one the proxy controls itself.</param>
/// <param name="Value">Its value, at most 4096 characters, without line breaks.</param>
public sealed record ProxyHeader(string Name, string Value);

/// <summary>Where a proxy host forwards to.</summary>
/// <param name="Scheme"><c>http</c> or <c>https</c>.</param>
/// <param name="Host">A DNS name or IP address.</param>
/// <param name="Port">1 to 65535.</param>
/// <param name="Tls">How an HTTPS target's certificate is checked.</param>
/// <param name="OnBox">The administrator confirmed that the target is a service of this box itself.</param>
public sealed record ProxyTarget(string Scheme, string Host, int Port, ProxyTargetTls Tls, bool OnBox);

/// <summary>A user of the host's Basic Auth.</summary>
/// <param name="Username">1 to 64 characters without a colon.</param>
/// <param name="Password">The password; leave it out to keep the stored one of an existing user.</param>
public sealed record ProxyBasicAuthUser(string Username, string? Password = null);

/// <summary>Who may pass the host's Pithyx sign-in.</summary>
/// <param name="Required">Visitors sign in with their account on the box first.</param>
/// <param name="Users">User names allowed besides the groups; administrators always are.</param>
/// <param name="Groups">Group names whose members are allowed. With no users and no groups, everybody who may sign in is.</param>
public sealed record ProxySignInRule(bool Required, IReadOnlyList<string> Users, IReadOnlyList<string> Groups);

/// <summary>A proxy host as an administrator saves it.</summary>
/// <param name="Domains">1 to 50 DNS names, a leading <c>*.</c> for any one label in front; no name of another host.</param>
/// <param name="Enabled">The host answers; a disabled one is treated as unknown.</param>
/// <param name="Target">Where requests go.</param>
/// <param name="WebSockets">Pass WebSocket upgrades through.</param>
/// <param name="Http2">Offer HTTP/2 to browsers on 443.</param>
/// <param name="Certificate">Which certificate the host presents on 443.</param>
/// <param name="ForceHttps">Redirect plain HTTP on 80 to HTTPS on 443.</param>
/// <param name="Hsts">Send <c>Strict-Transport-Security</c> on HTTPS; browsers then refuse plain HTTP and any certificate warning for a year.</param>
/// <param name="ForwardedHeaders">Send <c>X-Forwarded-For</c>, <c>-Proto</c> and <c>-Host</c>; incoming ones are always dropped.</param>
/// <param name="PreserveHost">Send the browser's <c>Host</c> instead of the target's.</param>
/// <param name="HostHeader">A <c>Host</c> of your own; overrides <paramref name="PreserveHost"/>.</param>
/// <param name="RequestHeaders">Headers set on the request to the target, at most 20.</param>
/// <param name="ResponseHeaders">Headers set on the answer to the browser, at most 20.</param>
/// <param name="MaxBodyMegabytes">The largest upload in MB (1 to 1048576); none for no limit.</param>
/// <param name="ConnectTimeoutSeconds">How long connecting to the target may take, 1 to 300 seconds.</param>
/// <param name="ResponseTimeoutSeconds">How long the target may stay silent, 1 to 86400 seconds.</param>
/// <param name="AllowAddresses">Only these addresses or CIDR ranges get in; empty for everybody.</param>
/// <param name="DenyAddresses">These addresses or ranges never get in.</param>
/// <param name="BasicAuth">Users of Basic Auth, at most 20; empty for none.</param>
/// <param name="SignIn">The Pithyx sign-in in front of the host.</param>
/// <param name="AccessLog">Log every request in the Log Center under <c>proxy/&lt;first domain&gt;</c>.</param>
/// <param name="CertificatePem">With <see cref="ProxyCertificateSource.Custom"/>: the certificate and its chain in PEM; leave it out to keep the uploaded one.</param>
/// <param name="PrivateKeyPem">Its unencrypted private key in PEM.</param>
public sealed record ProxyHostRequest(
    IReadOnlyList<string> Domains,
    bool Enabled,
    ProxyTarget Target,
    bool WebSockets,
    bool Http2,
    ProxyCertificateSource Certificate,
    bool ForceHttps,
    bool Hsts,
    bool ForwardedHeaders,
    bool PreserveHost,
    string? HostHeader,
    IReadOnlyList<ProxyHeader> RequestHeaders,
    IReadOnlyList<ProxyHeader> ResponseHeaders,
    int? MaxBodyMegabytes,
    int ConnectTimeoutSeconds,
    int ResponseTimeoutSeconds,
    IReadOnlyList<string> AllowAddresses,
    IReadOnlyList<string> DenyAddresses,
    IReadOnlyList<ProxyBasicAuthUser> BasicAuth,
    ProxySignInRule SignIn,
    bool AccessLog,
    string? CertificatePem = null,
    string? PrivateKeyPem = null);

/// <summary>A proxy host as Settings shows it.</summary>
/// <param name="Id">Its number on the box.</param>
/// <param name="Domains">The names it answers.</param>
/// <param name="Enabled">It answers.</param>
/// <param name="Target">Where requests go.</param>
/// <param name="WebSockets">WebSocket upgrades pass.</param>
/// <param name="Http2">HTTP/2 is offered on 443.</param>
/// <param name="Certificate">Which certificate it presents.</param>
/// <param name="Presented">The certificate it presents now; none when the box cannot make one (a name outside the authority's scope).</param>
/// <param name="ForceHttps">Plain HTTP is redirected to HTTPS.</param>
/// <param name="Hsts">HTTPS answers carry <c>Strict-Transport-Security</c>.</param>
/// <param name="ForwardedHeaders"><c>X-Forwarded-*</c> go to the target.</param>
/// <param name="PreserveHost">The browser's <c>Host</c> goes to the target.</param>
/// <param name="HostHeader">The <c>Host</c> of the administrator's choice, if any.</param>
/// <param name="RequestHeaders">Headers set on requests.</param>
/// <param name="ResponseHeaders">Headers set on answers.</param>
/// <param name="MaxBodyMegabytes">The upload limit in MB, or none.</param>
/// <param name="ConnectTimeoutSeconds">The connect timeout.</param>
/// <param name="ResponseTimeoutSeconds">The idle timeout of an answer.</param>
/// <param name="AllowAddresses">Only these get in, or everybody when empty.</param>
/// <param name="DenyAddresses">These never get in.</param>
/// <param name="BasicAuthUsers">The user names of the Basic Auth; passwords are never shown.</param>
/// <param name="SignIn">The Pithyx sign-in in front of the host.</param>
/// <param name="AccessLog">Requests are logged.</param>
/// <param name="LogSource">The Log Center's source of its requests.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="UpdatedAt">When it was last saved.</param>
/// <param name="Acme">With a certificate from the ACME authority: where it stands.</param>
public sealed record ProxyHost(
    long Id,
    IReadOnlyList<string> Domains,
    bool Enabled,
    ProxyTarget Target,
    bool WebSockets,
    bool Http2,
    ProxyCertificateSource Certificate,
    CertificateInfo? Presented,
    bool ForceHttps,
    bool Hsts,
    bool ForwardedHeaders,
    bool PreserveHost,
    string? HostHeader,
    IReadOnlyList<ProxyHeader> RequestHeaders,
    IReadOnlyList<ProxyHeader> ResponseHeaders,
    int? MaxBodyMegabytes,
    int ConnectTimeoutSeconds,
    int ResponseTimeoutSeconds,
    IReadOnlyList<string> AllowAddresses,
    IReadOnlyList<string> DenyAddresses,
    IReadOnlyList<string> BasicAuthUsers,
    ProxySignInRule SignIn,
    bool AccessLog,
    string LogSource,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    AcmeCertificateStatus? Acme);

/// <summary>A redirect host as an administrator saves it.</summary>
/// <param name="Domains">1 to 50 DNS names, as for a proxy host.</param>
/// <param name="Enabled">The host answers.</param>
/// <param name="Target">An absolute <c>http</c> or <c>https</c> URL.</param>
/// <param name="Status">301, 302, 307 or 308.</param>
/// <param name="PreservePath">Append the request's path and query to <paramref name="Target"/>.</param>
/// <param name="Certificate">Which certificate the host presents on 443.</param>
/// <param name="CertificatePem">With <see cref="ProxyCertificateSource.Custom"/>: the certificate and chain; leave it out to keep the uploaded one.</param>
/// <param name="PrivateKeyPem">Its unencrypted private key.</param>
public sealed record ProxyRedirectRequest(
    IReadOnlyList<string> Domains,
    bool Enabled,
    string Target,
    int Status,
    bool PreservePath,
    ProxyCertificateSource Certificate,
    string? CertificatePem = null,
    string? PrivateKeyPem = null);

/// <summary>A redirect host as Settings shows it.</summary>
/// <param name="Id">Its number on the box.</param>
/// <param name="Domains">The names it answers.</param>
/// <param name="Enabled">It answers.</param>
/// <param name="Target">Where it sends browsers.</param>
/// <param name="Status">The redirect's status code.</param>
/// <param name="PreservePath">The path and query are kept.</param>
/// <param name="Certificate">Which certificate it presents.</param>
/// <param name="Presented">The certificate it presents now, if the box can make one.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="UpdatedAt">When it was last saved.</param>
/// <param name="Acme">With a certificate from the ACME authority: where it stands.</param>
public sealed record ProxyRedirect(
    long Id,
    IReadOnlyList<string> Domains,
    bool Enabled,
    string Target,
    int Status,
    bool PreservePath,
    ProxyCertificateSource Certificate,
    CertificateInfo? Presented,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    AcmeCertificateStatus? Acme);

/// <summary>A stream as an administrator saves it.</summary>
/// <param name="Name">What it is for, at most 100 characters.</param>
/// <param name="Enabled">The port is open and forwards.</param>
/// <param name="Port">The box's port; not one of the box's own services, below 1024 only with <paramref name="PrivilegedPort"/>.</param>
/// <param name="Tcp">Forward TCP.</param>
/// <param name="Udp">Forward UDP.</param>
/// <param name="TargetHost">A DNS name or IP address.</param>
/// <param name="TargetPort">1 to 65535.</param>
/// <param name="PrivilegedPort">The administrator confirmed a port below 1024.</param>
public sealed record ProxyStreamRequest(string Name, bool Enabled, int Port, bool Tcp, bool Udp, string TargetHost, int TargetPort, bool PrivilegedPort = false);

/// <summary>A stream as Settings shows it.</summary>
/// <param name="Id">Its number on the box.</param>
/// <param name="Name">What it is for.</param>
/// <param name="Enabled">The port is open.</param>
/// <param name="Port">The box's port.</param>
/// <param name="Tcp">TCP is forwarded.</param>
/// <param name="Udp">UDP is forwarded.</param>
/// <param name="TargetHost">Where it forwards to.</param>
/// <param name="TargetPort">The target's port.</param>
/// <param name="Listening">The box listens on the port now; false while disabled or when another program holds it.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="UpdatedAt">When it was last saved.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "A stream of the reverse proxy, not a System.IO.Stream.")]
public sealed record ProxyStream(long Id, string Name, bool Enabled, int Port, bool Tcp, bool Udp, string TargetHost, int TargetPort, bool Listening, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Settings > Reverse proxy.</summary>
/// <param name="PlainPort">The port proxy hosts answer plain HTTP on (80 on a box); none in development unless set.</param>
/// <param name="TlsPort">The port proxy hosts answer HTTPS on (443 on a box); none without HTTPS.</param>
/// <param name="UnknownHosts">What a name no enabled host has gets.</param>
/// <param name="SignInAddress">Where the Pithyx sign-in of a host sends visitors; none for the requested name on 7484.</param>
/// <param name="Hosts">The proxy hosts.</param>
/// <param name="Redirects">The redirect hosts.</param>
/// <param name="Streams">The streams.</param>
public sealed record ProxySettings(
    int? PlainPort,
    int? TlsPort,
    UnknownHostAction UnknownHosts,
    string? SignInAddress,
    IReadOnlyList<ProxyHost> Hosts,
    IReadOnlyList<ProxyRedirect> Redirects,
    IReadOnlyList<ProxyStream> Streams);

/// <summary>The settings that apply to every host.</summary>
/// <param name="UnknownHosts">What a name no enabled host has gets.</param>
/// <param name="SignInAddress">An <c>https://</c> or <c>http://</c> origin the box's sign-in answers at, such as a proxy host in front of 7484; none for the requested name on 7484.</param>
public sealed record ProxyOptionsRequest(UnknownHostAction UnknownHosts, string? SignInAddress = null);

/// <summary>Whether a proxy host's target answers.</summary>
/// <param name="Reachable">The target answered with any HTTP status.</param>
/// <param name="Status">Its status code, if it answered.</param>
/// <param name="Error">Why not: <c>timeout</c>, <c>refused</c>, <c>dns</c>, <c>tls</c> or <c>other</c>.</param>
/// <param name="Milliseconds">How long the check took.</param>
public sealed record ProxyTargetStatus(bool Reachable, int? Status, string? Error, long Milliseconds);

/// <summary>A browser wants into a proxy host with the Pithyx sign-in.</summary>
/// <param name="Url">The address it asked for.</param>
public sealed record ProxyTicketRequest(string Url);

/// <summary>Where the browser goes next: the proxy host's sign-in address with a one-time ticket.</summary>
/// <param name="Url">The address to open.</param>
public sealed record ProxyTicket(string Url);
