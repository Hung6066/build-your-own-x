namespace Hope.Agent.Api.Security;

/// <summary>Configuration model for the token issuance subsystem.</summary>
internal sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Lifetime of issued access tokens in minutes. Default: 5.</summary>
    public int AccessTokenLifetimeMinutes { get; set; } = 5;

    /// <summary>Lifetime of refresh tokens in days. Default: 7.</summary>
    public int RefreshTokenLifetimeDays { get; set; } = 7;

    /// <summary>
    /// Static service-account credentials. Each entry corresponds to one machine client
    /// (e.g. clinical portal, mobile app, integration bus). In production, provision these
    /// through Key Vault rather than appsettings.json.
    /// </summary>
    public ServiceAccountEntry[] ServiceAccounts { get; set; } = [];
}

internal sealed class ServiceAccountEntry
{
    /// <summary>Machine-readable client identifier sent in the login request.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// SHA-256 hex digest (lower-case) of the client secret.
    /// Generate: <c>echo -n "MySecret" | sha256sum</c> or PowerShell
    /// <c>[System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes("MySecret"))).Replace("-","").ToLower()</c>
    /// </summary>
    public string SecretHash { get; set; } = "";

    /// <summary>Roles attached to access tokens issued for this service account.</summary>
    public string[] Roles { get; set; } = [];
}
