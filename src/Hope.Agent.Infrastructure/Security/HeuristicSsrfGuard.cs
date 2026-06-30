using Hope.Agent.Application.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// SSRF protection inspired by NemoClaw's <c>blueprint/ssrf.ts</c>.
/// Validates outbound URLs before the agent makes HTTP connections by blocking:
/// <list type="bullet">
///   <item>Non-HTTP/HTTPS schemes</item>
///   <item>Loopback addresses (127.0.0.0/8, ::1)</item>
///   <item>Private IP ranges (RFC 1918): 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16</item>
///   <item>Link-local / APIPA: 169.254.0.0/16</item>
///   <item>Known cloud-metadata endpoints (169.254.169.254, metadata.google.internal, etc.)</item>
/// </list>
/// Hostname-level checks run synchronously; IP-level checks use
/// <see cref="IPAddress.TryParse"/> (no DNS resolution needed for direct-IP URLs).
/// </summary>
internal sealed class HeuristicSsrfGuard(
    IOptionsMonitor<EgressPolicyOptions> egress,
    ILogger<HeuristicSsrfGuard> log) : ISsrfGuard
{
    // Cloud metadata and reserved hostnames that must never be reached from agent code
    private static readonly HashSet<string> BlockedHostnames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1",
        "::1",
        "0.0.0.0",
        "169.254.169.254",          // AWS / Azure / DO IMDS
        "metadata.google.internal", // GCP IMDS
        "metadata.internal",        // generic GCP alias
        "169.254.170.2",            // ECS container credentials endpoint
    };

    public SsrfCheckResult Validate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new SsrfCheckResult(false, $"Invalid URL format: {url}");
        return Validate(uri);
    }

    public SsrfCheckResult Validate(Uri uri)
    {
        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("SSRF guard blocked non-HTTP scheme: {Scheme} in {Url}", uri.Scheme, uri);
            return new SsrfCheckResult(false, $"Disallowed scheme '{uri.Scheme}' — only http/https permitted");
        }

        // Strip IPv6 brackets ("[::1]" → "::1")
        var host = uri.Host.Trim('[', ']');

        if (BlockedHostnames.Contains(host))
        {
            log.LogWarning("SSRF guard blocked known dangerous host: {Host}", host);
            return new SsrfCheckResult(false, $"Blocked host: {host}");
        }

        var opts = egress.CurrentValue;
        if (opts.RequireAllowlist && opts.AllowedHosts.Length > 0 && !opts.AllowedHosts.Any(allowed => HostMatches(host, allowed)))
        {
            log.LogWarning("SSRF guard blocked host outside egress allowlist: {Host}", host);
            return new SsrfCheckResult(false, $"Host outside egress allowlist: {host}");
        }

        // If the hostname is a literal IP address, check private ranges directly
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IsPrivateOrReserved(ip))
            {
                log.LogWarning("SSRF guard blocked private/reserved IP: {Ip}", ip);
                return new SsrfCheckResult(false, $"Private or reserved IP address blocked: {ip}");
            }
        }

        return new SsrfCheckResult(true, null);
    }

    private static bool HostMatches(string host, string allowed)
    {
        if (string.IsNullOrWhiteSpace(allowed)) return false;
        allowed = allowed.Trim();
        if (allowed.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = allowed[1..];
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return
                bytes[0] == 10                                          // 10.0.0.0/8
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)    // 172.16.0.0/12
                || (bytes[0] == 192 && bytes[1] == 168)                // 192.168.0.0/16
                || (bytes[0] == 169 && bytes[1] == 254)                // 169.254.0.0/16 link-local
                || bytes[0] == 0;                                       // 0.0.0.0/8
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal
                || ip.IsIPv6SiteLocal
                || ip.Equals(IPAddress.IPv6Loopback);
        }

        return false;
    }
}
