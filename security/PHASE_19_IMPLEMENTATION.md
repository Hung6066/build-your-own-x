# Phase 19 — Security Hardening Implementation Roadmap

**Status:** Not Started  
**Timeline:** 8 weeks (Critical: Weeks 1–2, High: Weeks 3–4, Medium: Weeks 5–8)  
**Build Status:** TBD (post-implementation)

---

## Phase 19A: Critical Security Fixes (Weeks 1–2)

### STEP A1: SecurityHeadersMiddleware (NEW FILE)

**File:** `src/Hope.Agent.Api/Security/SecurityHeadersMiddleware.cs`

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Api.Security;

/// <summary>
/// Adds comprehensive security headers to all HTTP responses.
/// Implements OWASP and NIST guidelines for API security.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, ILogger<SecurityHeadersMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var headers = ctx.Response.Headers;

        // Content-Security-Policy: Strict default, allow only necessary origins
        headers["Content-Security-Policy"] = "default-src 'self'; "
            + "script-src 'self' https://cdn.jsdelivr.net; "  // OpenAPI UI only
            + "style-src 'self' 'unsafe-inline'; "  // OpenAPI needs unsafe-inline
            + "img-src 'self' data: https:; "
            + "connect-src 'self' https://api.openai.com https://api.anthropic.com https://generativelanguage.googleapis.com; "
            + "font-src 'self' https://cdn.jsdelivr.net; "
            + "frame-ancestors 'none'; "
            + "base-uri 'self'; "
            + "form-action 'self'; "
            + "upgrade-insecure-requests";

        // Clickjacking protection
        headers["X-Frame-Options"] = "DENY";

        // MIME type sniffing prevention
        headers["X-Content-Type-Options"] = "nosniff";

        // XSS protection (legacy browsers)
        headers["X-XSS-Protection"] = "1; mode=block";

        // Referrer privacy
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Feature/Permissions policy — disable unused APIs
        headers["Permissions-Policy"] = "geolocation=(), "
            + "camera=(), "
            + "microphone=(), "
            + "payment=(), "
            + "usb=(), "
            + "magnetometer=(), "
            + "gyroscope=(), "
            + "accelerometer=(), "
            + "ambient-light-sensor=(), "
            + "encrypted-media=(), "
            + "fullscreen=(), "
            + "picture-in-picture=()";

        // HSTS: Enforce HTTPS for next 2 years, include subdomains
        if (ctx.Request.IsHttps)
            headers["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains; preload";

        // Expect-CT: Public key pinning notification (advanced)
        if (ctx.Request.IsHttps)
            headers["Expect-CT"] = "max-age=86400, enforce";

        // Remove server banner (don't advertise .NET/Kestrel)
        headers.Remove("Server");
        headers["Server"] = "Hope.Agent/1.0";

        // Add request ID for forensics/debugging
        var requestId = ctx.TraceIdentifier ?? Guid.CreateVersion7().ToString();
        headers["X-Request-Id"] = requestId;
        ctx.Items["RequestId"] = requestId;

        // No caching for API responses (prevent stale data leakage)
        headers["Cache-Control"] = "no-store, no-cache, must-revalidate, proxy-revalidate, max-age=0";
        headers["Pragma"] = "no-cache";
        headers["Expires"] = "0";

        await next(ctx);
    }
}
```

**Register in Program.cs:**

```csharp
// BEFORE routing/authentication
app.UseMiddleware<SecurityHeadersMiddleware>();
```

---

### STEP A2: Enforce HTTPS + HSTS

**File:** `src/Hope.Agent.Api/Program.cs` (Update)

```csharp
// Around line 109, JWT Bearer config
var jwt = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        // Production: Require HTTPS for token transport
        o.RequireHttpsMetadata = !app.Environment.IsDevelopment();

        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwt["Secret"] ?? "dev-secret-please-change-32+chars-min")),
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        // Strict SSL verification in production
        if (app.Environment.IsProduction())
        {
            o.Backchannel = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, err) =>
                    err == System.Net.Security.SslPolicyErrors.None
            };
        }
    });

// AFTER app.Build()
// Require HTTPS redirect for all requests (except /healthz)
if (app.Environment.IsProduction())
{
    app.UseHttpsRedirection();
}
```

---

### STEP A3: CORS Configuration

**File:** `src/Hope.Agent.Api/Program.cs` (Add)

```csharp
// After AddAuthorization
builder.Services.AddCors(o =>
{
    var corsDomains = builder.Configuration.GetSection("Cors:AllowedDomains").Get<string[]>()
        ?? new[] { "http://localhost:3000", "http://localhost:5000" };

    o.AddPolicy("StrictCors", policy => policy
        .WithOrigins(corsDomains)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials()
        .WithExposedHeaders("X-Pagination-Count", "X-Request-Id", "X-RateLimit-Remaining")
        .SetPreflightMaxAge(TimeSpan.FromHours(1)));

    // Also define an anonymous policy for public endpoints
    o.AddPolicy("PublicCors", policy => policy
        .AllowAnyOrigin()
        .WithMethods("GET")
        .AllowAnyHeader()
        .SetPreflightMaxAge(TimeSpan.FromHours(1)));
});

// After middleware setup
app.UseCors("StrictCors");  // Default: strict
```

**Update endpoints:**

```csharp
// For public endpoints, use:
grp.MapGet("/health", () => "OK")
    .WithName("Health")
    .AllowAnonymous()
    .WithMetadata(new EnableCorsAttribute("PublicCors"));
```

---

### STEP A4: PHI-Aware Logging Middleware

**File:** `src/Hope.Agent.Api/Security/AuditLoggingMiddleware.cs` (NEW)

```csharp
using Hope.Agent.Application.Audit;
using Hope.Agent.Application.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using System.Security.Claims;

namespace Hope.Agent.Api.Security;

/// <summary>
/// Middleware that logs all API access to audit trail.
/// Redacts PHI via IPhiRedactor before persisting logs.
/// </summary>
public sealed class AuditLoggingMiddleware(
    RequestDelegate next,
    IAuditSink auditSink,
    IPhiRedactor phiRedactor,
    ILogger<AuditLoggingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var startTime = DateTimeOffset.UtcNow;
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        var userEmail = ctx.User.FindFirstValue(ClaimTypes.Email) ?? "unknown@example.com";
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = ctx.Request.Headers["User-Agent"].ToString();
        var requestId = ctx.TraceIdentifier ?? Guid.CreateVersion7().ToString();

        // Push context for all logs in this request
        using (LogContext.PushProperty("UserId", userId))
        using (LogContext.PushProperty("Email", userEmail))
        using (LogContext.PushProperty("Ip", ip))
        using (LogContext.PushProperty("RequestId", requestId))
        {
            try
            {
                await next(ctx);
            }
            finally
            {
                // Log after response
                var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
                var path = ctx.Request.Path.ToString();
                var method = ctx.Request.Method;
                var statusCode = ctx.Response.StatusCode;
                var success = statusCode < 400;

                // Redact path if it contains PHI
                var redactedPath = phiRedactor.Redact(path);

                log.LogInformation(
                    "API request: {Method} {Path} → {StatusCode} in {DurationMs}ms from {Ip}",
                    method, redactedPath, statusCode, duration, ip);

                // Persist to immutable audit table
                await auditSink.LogAsync(new AuditEvent
                {
                    Id = Guid.CreateVersion7(),
                    UserId = Guid.TryParse(userId, out var uid) ? uid : Guid.Empty,
                    Timestamp = DateTimeOffset.UtcNow,
                    EventType = "api_access",
                    Resource = redactedPath,
                    Action = method,
                    Success = success,
                    ErrorReason = success ? null : $"HTTP {statusCode}",
                    IpAddress = ip,
                    UserAgent = userAgent,
                    Metadata = new Dictionary<string, string>
                    {
                        ["duration_ms"] = duration.ToString("F0"),
                        ["status_code"] = statusCode.ToString(),
                    }
                }, CancellationToken.None);
            }
        }
    }
}
```

**Register in Program.cs:**

```csharp
// After SecurityHeadersMiddleware
app.UseMiddleware<AuditLoggingMiddleware>();
```

---

### STEP A5: Move Secrets to Key Vault (appsettings.json)

**File:** `src/Hope.Agent.Api/appsettings.json` (Update)

```json
{
  "Jwt": {
    "Issuer": "https://hope.hospital.com",
    "Audience": "hope-api",
    // Remove: "Secret": "...",  ← DELETE FROM CODE
    "Algorithm": "HS256"
  },
  "LLM": {
    "DefaultChatProvider": "openai",
    "OpenAI": {
      "BaseUrl": "https://api.openai.com/v1",
      // Remove: "ApiKey": "sk-...",  ← USE KEY VAULT INSTEAD
      "Model": "gpt-4-turbo"
    }
    // ... similar for other providers
  },
  "KeyVault": {
    "VaultName": "hope-agent-kv",
    "Enabled": true // Disabled in dev
  },
  "Cors": {
    "AllowedDomains": [
      "https://hope.hospital.com",
      "https://hope-web.azurewebsites.net"
    ]
  }
}
```

**Update Program.cs:**

```csharp
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

// AFTER configuration builders
if (builder.Environment.IsProduction())
{
    var kvUri = $"https://{builder.Configuration["KeyVault:VaultName"]}.vault.azure.net/";
    var credential = new DefaultAzureCredential();

    builder.Configuration.AddAzureKeyVault(
        new Uri(kvUri),
        credential,
        new KeyVaultSecretManager());

    log.LogInformation("KeyVault configured: {Uri}", kvUri);
}
```

---

## Phase 19B: High-Priority Fixes (Weeks 3–4)

### STEP B1: JWT Key Rotation

**File:** `src/Hope.Agent.Application/Security/IJwtKeyProvider.cs` (NEW)

```csharp
using Microsoft.IdentityModel.Tokens;

namespace Hope.Agent.Application.Security;

public interface IJwtKeyProvider
{
    /// <summary>Returns current + previous (grace period) signing keys.</summary>
    Task<(SecurityKey Current, SecurityKey? Previous)> GetSigningKeysAsync(CancellationToken ct);

    /// <summary>Key ID for JWT header.</summary>
    Task<string> GetKeyIdAsync(CancellationToken ct);
}
```

**File:** `src/Hope.Agent.Infrastructure/Security/RotatingJwtKeyProvider.cs` (NEW)

```csharp
using Azure.Security.KeyVault.Secrets;
using Hope.Agent.Application.Security;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Hope.Agent.Infrastructure.Security;

public sealed class RotatingJwtKeyProvider(
    SecretClient vault,
    IMemoryCache cache,
    ILogger<RotatingJwtKeyProvider> log) : IJwtKeyProvider
{
    private const string CacheKey = "jwt:signing-keys";
    private const int RefreshIntervalHours = 1;  // Refresh cache every hour
    private const int RotationIntervalDays = 90;  // Rotate every 90 days
    private const int GracePeriodDays = 2;  // Accept old key for 2 days

    public async Task<(SecurityKey Current, SecurityKey? Previous)> GetSigningKeysAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out (SecurityKey, SecurityKey?)? cached))
            return cached!.Value;

        // Fetch from Key Vault
        var currentSecret = await vault.GetSecretAsync("hope-agent-jwt-signing-key--current", cancellationToken: ct);
        var previousSecret = await vault.GetSecretAsync("hope-agent-jwt-signing-key--previous", cancellationToken: ct);

        var current = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(currentSecret.Value.Value));
        var previous = previousSecret?.Value?.Value != null
            ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(previousSecret.Value.Value))
            : null;

        // Cache for 1 hour
        var cacheOptions = new MemoryCacheEntryOptions
            .SetAbsoluteExpiration(TimeSpan.FromHours(RefreshIntervalHours));

        cache.Set(CacheKey, (current, previous), cacheOptions);

        log.LogInformation("JWT signing keys refreshed from Key Vault");

        return (current, previous);
    }

    public async Task<string> GetKeyIdAsync(CancellationToken ct)
    {
        const string keyIdKey = "jwt:key-id";

        if (cache.TryGetValue(keyIdKey, out string? keyId))
            return keyId!;

        var secret = await vault.GetSecretAsync("hope-agent-jwt-key-id", cancellationToken: ct);
        var id = secret.Value.Value;

        cache.Set(keyIdKey, id, TimeSpan.FromHours(24));

        return id;
    }
}
```

**Register in Program.cs:**

```csharp
var credential = new DefaultAzureCredential();
var kvUri = $"https://{builder.Configuration["KeyVault:VaultName"]}.vault.azure.net/";
var vault = new SecretClient(new Uri(kvUri), credential);

builder.Services.AddSingleton(vault);
builder.Services.AddSingleton<IJwtKeyProvider, RotatingJwtKeyProvider>();
```

---

### STEP B2: Input Validation with Attributes

**File:** `src/Hope.Agent.Api/Endpoints/AgentEndpoints.cs` (Update)

```csharp
using System.ComponentModel.DataAnnotations;

public sealed record AgentChatRequest(
    [StringLength(8000, MinimumLength = 1)]
    [RegularExpression(@"^[\p{L}\p{N}\p{P}\p{Z}]+$", ErrorMessage = "Message contains invalid characters")]
    string Message,

    Guid? ConversationId = null,
    Dictionary<string, string>? Context = null);

// In endpoint handler:
grp.MapPost("/chat", async (
    [FromBody] AgentChatRequest req,
    [FromServices] IAgentRuntime runtime,
    HttpContext http,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    // Manual validation (minimal API doesn't auto-validate)
    if (string.IsNullOrWhiteSpace(req.Message))
        return Results.BadRequest("Message required");

    if (req.Message.Length > 8000)
        return Results.BadRequest("Message exceeds 8000 chars");

    // Check for common injection patterns
    var injectionPatterns = new[] { "DROP TABLE", "DELETE FROM", "'; --" };
    if (injectionPatterns.Any(req.Message.Contains))
        return Results.BadRequest("Suspicious SQL patterns detected");

    // ... proceed
});
```

---

### STEP B3: Database Encryption (PostgreSQL)

**File:** `src/Hope.Agent.Infrastructure/DependencyInjection.cs` (Update)

```csharp
services.AddDbContextPool<AgentDbContext>(o =>
{
    var connStr = cfg.GetConnectionString("Postgres") ?? throw new InvalidOperationException();

    // Force SSL for Postgres
    if (!connStr.Contains("SSL Mode"))
        connStr += ";SSL Mode=Require;Trust Server Certificate=false";

    o.UseNpgsql(connStr, npg =>
    {
        npg.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelaySeconds: 10);
        npg.CommandTimeout(30);
    });

    // Log SQL in development only
    if (!cfg.IsDevelopment())
        o.EnableSensitiveDataLogging(false);

}, poolSize: 128);
```

---

### STEP B4: Redis TLS Configuration

**File:** `src/Hope.Agent.Infrastructure/DependencyInjection.cs` (Update)

```csharp
services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var redisConnStr = cfg.GetConnectionString("Redis") ?? "localhost:6379";

    var options = ConfigurationOptions.Parse(redisConnStr);

    // Enable TLS if not in development
    if (!cfg.IsDevelopment())
    {
        options.Ssl = true;
        options.TrustCertificate = false;  // Validate cert
        options.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
    }

    return ConnectionMultiplexer.Connect(options);
});
```

---

### STEP B5: Qdrant HTTPS Configuration

**File:** `src/Hope.Agent.Infrastructure/DependencyInjection.cs` (Update)

```csharp
var qdrant = cfg.GetSection("Qdrant").Get<QdrantOptions>() ?? new QdrantOptions();

// Enforce HTTPS in production
if (!cfg.IsDevelopment() && !qdrant.Host.StartsWith("https://"))
{
    qdrant.Host = $"https://{qdrant.Host}";
    log.LogInformation("Enforced HTTPS for Qdrant: {Host}", qdrant.Host);
}

services.AddSingleton(qdrant);
services.AddSingleton(_ => new QdrantClient(qdrant.Host, qdrant.Port, apiKey: qdrant.ApiKey));
```

---

### STEP B6: Complete Audit Logging Schema + Endpoints

**File:** `src/Hope.Agent.Infrastructure/Persistence/AgentDbContext.cs` (Add DbSet)

```csharp
public DbSet<AuditLogEntity> AuditLogs { get; set; } = null!;
```

**Migration:**

```bash
dotnet ef migrations add Phase19_AuditLogging \
  --project src/Hope.Agent.Infrastructure \
  --startup-project src/Hope.Agent.Api

dotnet ef database update
```

**SQL (for reference):**

```sql
CREATE TABLE audit_logs (
    id UUID PRIMARY KEY,
    user_id UUID,
    timestamp TIMESTAMPTZ NOT NULL,
    event_type VARCHAR(128) NOT NULL,
    resource VARCHAR(512) NOT NULL,
    action VARCHAR(32) NOT NULL,
    success BOOLEAN NOT NULL,
    error_reason TEXT,
    redacted_content TEXT,
    ip_address VARCHAR(45),
    user_agent TEXT,
    metadata JSONB,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_audit_user_timestamp ON audit_logs(user_id, timestamp DESC);
CREATE INDEX idx_audit_event_type ON audit_logs(event_type);
CREATE INDEX idx_audit_resource ON audit_logs(resource);
CREATE INDEX idx_audit_timestamp ON audit_logs(timestamp DESC);
```

---

## Phase 19C: API Versioning + Hardening (Weeks 5–8)

### (Additional steps for API versioning, DI improvements, etc.)

---

## Verification Checklist

### After Phase 19A (Week 2):

- [ ] SecurityHeadersMiddleware compiles + endpoints return security headers
- [ ] HTTPS enforced in production (verify via curl `curl -i https://...`)
- [ ] CORS policy configured + preflight requests work
- [ ] Secrets removed from code + Key Vault reads successfully
- [ ] Audit logs flowing to DB + visible via monitoring

### After Phase 19B (Week 4):

- [ ] JWT keys rotate on schedule (verify Key Vault history)
- [ ] PostgreSQL requires SSL (check connection fails without it)
- [ ] Redis accepts only TLS connections
- [ ] Input validation rejects malicious payloads (test SQL injection attempts)
- [ ] Rate limiter blocks spurious X-Forwarded-For headers

### After Phase 19C (Week 8):

- [ ] API versioning headers present + respected
- [ ] Dependencies scanned (no critical CVEs)
- [ ] OWASP ZAP scan: 0 high/critical findings
- [ ] HIPAA audit: compliant with §164.312

---

## Configuration (appsettings.Production.json)

```json
{
  "KeyVault": {
    "VaultName": "hope-agent-prod-kv",
    "Enabled": true
  },
  "Cors": {
    "AllowedDomains": [
      "https://hope.hospital.com",
      "https://api.hope.hospital.com",
      "https://hope-app.azurewebsites.net"
    ]
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Hope.Agent": "Debug"
    }
  },
  "AuditLog": {
    "Enabled": true,
    "RetentionDays": 2555 // 7 years for HIPAA
  }
}
```

---

## Build & Test Commands

```bash
# Build with warnings-as-errors
dotnet build Hope.Agent.sln -p:TreatWarningsAsErrors=true

# Run security checks
dotnet security-audit

# Generate SBOM (Software Bill of Materials)
sbom-tool generate -b . -bc src -ps PackageSource

# Test endpoints with security headers
curl -v -H "Authorization: Bearer $TOKEN" https://localhost:5001/v1/agent/chat

# Verify headers present
curl -i https://localhost:5001/healthz | grep -i "strict-transport-security"
```

---

**Next:**  
Proceed to Phase 19A implementation (Week 1–2). Then proceed to Phase 19B (Week 3–4).
