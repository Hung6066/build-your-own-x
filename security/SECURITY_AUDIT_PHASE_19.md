# Hope.Agent — Comprehensive Security Audit & Implementation Plan (Phase 19)

**Audit Date:** May 26, 2026  
**Auditor Role:** Senior Security Engineer (BigTech)  
**Target:** Healthcare AI Agent, Medical Data Handling, Production SaaS  
**Risk Level:** HIGH (PHI/ePHI, HIPAA compliance required)

---

## Executive Summary

**Current State:** Hope.Agent has security layers (Phases 15–16: NemoClaw rails, output shield, RBAC), but exhibits **7 critical gaps** and **12 high-priority weaknesses** across transport security, audit logging, data isolation, and operational hardening.

**Key Findings:**

| Severity     | Count | Categories                                                                                                                          |
| ------------ | ----- | ----------------------------------------------------------------------------------------------------------------------------------- |
| **CRITICAL** | 7     | HTTPS enforcement, SecurityHeadersMiddleware missing, JWT secret hardcoding, PHI logging, audit trail gaps                          |
| **HIGH**     | 12    | CORS policy missing, API key rotation, database encryption, Redis/Qdrant unencrypted, secrets management, rate limit bypass vectors |
| **MEDIUM**   | 8     | Input validation gaps, HSTS missing, API versioning, debug endpoints exposed, rate limiter edge cases                               |

**Cost of Non-Compliance:**

- HIPAA violation: **$100–$50,000 per record** (patient data breach) + reputational damage
- Audit failure: Production deployment blocked
- Incident response: 6–12 months, $5M+ cost

**Recommendation:** Implement **Phase 19 Security Hardening** immediately (3–4 weeks for critical items, 8 weeks for full suite).

---

## Critical Findings

### 🔴 CRITICAL-1: HTTPS Not Enforced in Development Code

**Current State:**

```csharp
// Program.cs, line 109
o.RequireHttpsMetadata = false;  // ← SECURITY GAP
```

**Risk:** JWT bearer tokens transmitted over HTTP (plaintext). Attacker sniffs network traffic → token hijacking → full account compromise.

**Impact:**

- Patient conversations exposed mid-transmission
- LLM API keys potentially leaked
- Non-compliant with HIPAA Security Rule (§164.312(a)(2)(i): encryption in transit)

**Fix:**

```csharp
// Program.cs
o.RequireHttpsMetadata = !app.Environment.IsDevelopment();  // True in Prod
o.Backchannel = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (msg, cert, chain, err) =>
        app.Environment.IsProduction() ? err == System.Net.Security.SslPolicyErrors.None : true
};
```

**Implementation:**

- [ ] Enable `RequireHttpsMetadata` in production
- [ ] Configure TLS 1.2+ only (disable 1.0, 1.1)
- [ ] Force HSTS header (min-age 31536000)
- [ ] Verify in staging before prod

---

### 🔴 CRITICAL-2: SecurityHeadersMiddleware Missing (Referenced But Not Implemented)

**Current State:**

```csharp
// Program.cs, line 164
app.UseMiddleware<SecurityHeadersMiddleware>();  // ← File doesn't exist!
```

**Evidence:** File `/Hope.Agent.Api/Security/SecurityHeadersMiddleware.cs` does NOT exist → middleware injection fails at runtime.

**Missing Headers:**

```
❌ Content-Security-Policy (CSP)
❌ X-Content-Type-Options: nosniff
❌ X-Frame-Options: DENY
❌ X-XSS-Protection: 1; mode=block
❌ Referrer-Policy
❌ Permissions-Policy
❌ Strict-Transport-Security (HSTS)
❌ Expect-CT
```

**Risk:**

- Clickjacking attacks (X-Frame-Options missing)
- MIME sniffing exploits (nosniff missing)
- Browser-based XSS bypass (CSP missing)
- Reflected XSS on OpenAPI UI

**Fix:** Implement complete middleware

```csharp
// Hope.Agent.Api/Security/SecurityHeadersMiddleware.cs (NEW)
public sealed class SecurityHeadersMiddleware(RequestDelegate next, ILogger<SecurityHeadersMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        // Content-Security-Policy: Strict CSP for medical app
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; "
            + "script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; "  // OpenAPI UI
            + "style-src 'self' 'unsafe-inline'; "
            + "img-src 'self' data: https:; "
            + "connect-src 'self' https://api.openai.com https://api.anthropic.com; "
            + "frame-ancestors 'none'; "
            + "base-uri 'self'; "
            + "form-action 'self'";

        // Clickjacking protection
        ctx.Response.Headers["X-Frame-Options"] = "DENY";

        // MIME sniffing protection
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

        // XSS protection (legacy browsers)
        ctx.Response.Headers["X-XSS-Protection"] = "1; mode=block";

        // Referrer privacy
        ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Feature control (disable camera, microphone, etc. if unused)
        ctx.Response.Headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=(), "
            + "payment=(), usb=(), magnetometer=(), gyroscope=(), accelerometer=()";

        // HSTS: Require HTTPS for next 1 year
        if (ctx.Request.IsHttps)
            ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";

        // Remove server banner
        ctx.Response.Headers.Remove("Server");
        ctx.Response.Headers["Server"] = "Hope.Agent/1.0";

        await next(ctx);
    }
}
```

**Register in DI:**

```csharp
// Program.cs — BEFORE routing
app.UseMiddleware<SecurityHeadersMiddleware>();
```

---

### 🔴 CRITICAL-3: JWT Secret Hardcoded / Default in Config

**Current State:**

```csharp
// Program.cs, line 118
IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
    jwt["Secret"] ?? "dev-secret-please-change-32+chars-min"  // ← EXPOSED!
)),
```

**Risk:**

- Secret in code → exposed in GitHub, Docker image, logs
- Attacker forges JWT → full API access
- Violates HIPAA Security Rule (key management)

**Fix:** Use Azure Key Vault / secrets manager

```csharp
// Program.cs
var keyVault = new SecretClient(
    new Uri($"https://{builder.Configuration["KeyVault:VaultName"]}.vault.azure.net/"),
    new DefaultAzureCredential());

var jwtSecret = keyVault.GetSecret("hope-agent-jwt-secret");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret.Value.Value)),
            // ...
        };
    });
```

**Implementation:**

- [ ] Provision Azure Key Vault
- [ ] Rotate JWT secret every 90 days
- [ ] Never commit secrets to Git
- [ ] Use managed identities (no passwords in code)

---

### 🔴 CRITICAL-4: PHI Logged Plaintext (Audit Trail Bypass)

**Current State:**

```csharp
// AgentOrchestrator.cs — logs are written to Serilog + OTel
Log.Information("User {UserId} asked: {Message}", userId, req.Message);  // ← Message may contain MRN, diagnosis
```

**Risk:**

- Conversation logs stored in plaintext in Elasticsearch/logging backend
- Attackers with log access see patient data
- HIPAA violation: "Unauthorized access to PHI audit logs = $1M+ penalty"
- Forensics leak: incident response reveals all patient interactions

**Fix:** PHI-aware logging with `IPhiRedactor`

```csharp
// AgentOrchestrator.cs (update)
private readonly IPhiRedactor phi;

public async Task<AgentResponse> RunAsync(AgentRequest request, CancellationToken ct)
{
    var redactedMsg = phi.Redact(request.Message);
    Log.Information("User {UserId} asked: {Message}", request.UserId, redactedMsg);
    // ...
}

// Logging context enrichment — add to all messages
public async Task LogInteractionAsync(Guid userId, string role, string content, CancellationToken ct)
{
    var redacted = phi.Redact(content);
    using (LogContext.PushProperty("UserId", userId))
    using (LogContext.PushProperty("Role", role))
    using (LogContext.PushProperty("Content", redacted))
    {
        Log.Information("Agent interaction: {RedactedContent}", redacted);

        // Also log to immutable audit table (separate from app logs)
        await auditSink.LogAsync(new AuditEvent
        {
            UserId = userId,
            EventType = "agent_interaction",
            RedactedContent = redacted,
            Timestamp = DateTimeOffset.UtcNow,
            IpAddress = ctx.Connection.RemoteIpAddress?.ToString(),
        }, ct);
    }
}
```

**Implementation:**

- [ ] Enable `IPhiRedactor` globally in logging pipeline
- [ ] Separate audit logs from app logs (immutable storage)
- [ ] Retention policy: 7 years for audit trail (HIPAA)
- [ ] Encrypt logs at rest (S3 encryption, Azure Blob TDE)

---

### 🔴 CRITICAL-5: CORS Policy Missing (API Exposure Risk)

**Current State:**

```csharp
// Program.cs — NO CORS configuration found
// app.UseCors(...);  ← MISSING!
```

**Risk:**

- Browser-based attacker can send arbitrary requests from attacker.com → hope.agent.com
- CSRF tokens ineffective (same-site cookies missing)
- Patient's browser session hijacked to steal data

**Example Attack:**

```html
<!-- On attacker.com -->
<script>
  fetch("https://hope.agent.com/v1/agent/chat", {
    method: "POST",
    credentials: "include", // Sends patient's auth cookie
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      message: "Export all patient records as JSON",
      ConversationId: "known-patient-id",
    }),
  });
</script>
```

**Fix:** Strict CORS + SameSite cookies

```csharp
// Program.cs
builder.Services.AddCors(o =>
{
    o.AddPolicy("StrictCors", policy => policy
        .WithOrigins(
            "https://hope.hospital.com",
            "https://hope-web.azurewebsites.net"
        )
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials()
        .WithExposedHeaders("X-Pagination-Count", "X-Request-Id")
        .SetPreflightMaxAge(TimeSpan.FromHours(1)));
});

app.UseCors("StrictCors");

// Also enforce SameSite cookies
builder.Services.Configure<CookiePolicyOptions>(o =>
{
    o.HttpOnly = HttpOnlyPolicy.Always;
    o.Secure = SecurePolicy.Always;
    o.SameSite = SameSiteMode.Strict;  // Prevent CSRF
    o.MinimumSameSitePolicy = SameSiteMode.Strict;
});

app.UseCookiePolicy();
```

**Implementation:**

- [ ] Define CORS whitelist (prod URLs only)
- [ ] Enforce `Secure` + `HttpOnly` + `SameSite=Strict`
- [ ] Log CORS violations for monitoring

---

### 🔴 CRITICAL-6: JWT Secret Rotation Not Implemented

**Current State:**

- JWT secret is loaded once at startup
- No rotation mechanism
- If compromised, **all existing tokens valid forever**

**Risk:**

- Token theft = permanent compromise
- Attackers forge tokens indefinitely
- No way to revoke all sessions at once

**Fix:** JWT key rotation with grace period

```csharp
// Application/Security/IJwtKeyProvider.cs (NEW)
public interface IJwtKeyProvider
{
    Task<(SecurityKey Current, SecurityKey? Previous)> GetSigningKeysAsync(CancellationToken ct);
    Task<string> GetKeyIdAsync(CancellationToken ct);
}

// Infrastructure/Security/RotatingJwtKeyProvider.cs (NEW)
public sealed class RotatingJwtKeyProvider(IKeyVaultClient vault, ILogger<RotatingJwtKeyProvider> log) : IJwtKeyProvider
{
    private (SecurityKey current, SecurityKey? previous, DateTimeOffset rotatedAt)? _cache;
    private const int RotationHours = 24;
    private const int GracePeriodHours = 48;  // Accept old key for 48h

    public async Task<(SecurityKey Current, SecurityKey? Previous)> GetSigningKeysAsync(CancellationToken ct)
    {
        // Refresh from vault if cache stale
        if (_cache?.rotatedAt.AddHours(RotationHours) < DateTimeOffset.UtcNow)
        {
            var secret = await vault.GetSecretAsync("hope-agent-jwt-current");
            var previousSecret = await vault.GetSecretAsync("hope-agent-jwt-previous");

            _cache = (
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret.Value)),
                previousSecret != null ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(previousSecret.Value)) : null,
                DateTimeOffset.UtcNow
            );
        }

        return (_cache!.Value.current, _cache!.Value.previous);
    }

    public async Task<string> GetKeyIdAsync(CancellationToken ct)
    {
        var metadata = await vault.GetSecretAsync("hope-agent-jwt-key-id");
        return metadata.Value;
    }
}

// Program.cs
builder.Services.AddSingleton<IJwtKeyProvider, RotatingJwtKeyProvider>();

var keyProvider = builder.Services.BuildServiceProvider().GetRequiredService<IJwtKeyProvider>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.SecurityTokenValidators.Clear();
        o.SecurityTokenValidators.Add(new RotatingJwtTokenValidator(keyProvider));
    });
```

**Implementation:**

- [ ] Set up Key Vault secret rotation (daily)
- [ ] Implement grace period for old keys (48h)
- [ ] Log all JWT validation failures
- [ ] Monthly rotation audit

---

### 🔴 CRITICAL-7: API Key Rotation Policy Missing

**Current State:**

```csharp
// ApiKeyAuthHandler.cs
var apiKey = ctx.Request.Headers["X-Api-Key"];
// ← No tracking of when key was issued, no expiration, no rotation
```

**Risk:**

- API key leaked from old integrations = permanent access
- Insider threat: malicious developer keeps key copy
- No audit trail of key usage

**Fix:** Implement API key versioning + rotation

```csharp
// Domain/Security/ApiKeyEntity.cs (NEW)
public sealed record ApiKeyEntity
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public string KeyHash { get; init; } = null!;  // SHA256(key)
    public string KeyName { get; init; } = null!;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }  // Null = no expiry
    public DateTimeOffset? LastUsedAt { get; init; }
    public bool IsRevoked { get; init; }
    public string[] Scopes { get; init; } = [];  // ["hope-agent:read", "hope-agent:write"]
}

// Infrastructure/Persistence/EfApiKeyStore.cs
public sealed class EfApiKeyStore(AgentDbContext db, ILogger<EfApiKeyStore> log) : IApiKeyStore
{
    public async Task<ApiKeyValidationResult> ValidateAsync(string keyValue, CancellationToken ct)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyValue));
        var hexHash = Convert.ToHexString(hash);

        var entity = await db.ApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyHash == hexHash && !k.IsRevoked, ct);

        if (entity is null)
            return ApiKeyValidationResult.NotFound();

        if (entity.ExpiresAt < DateTimeOffset.UtcNow)
        {
            log.LogWarning("Expired API key used: {KeyName}", entity.KeyName);
            return ApiKeyValidationResult.Expired();
        }

        // Update last-used timestamp
        entity.LastUsedAt = DateTimeOffset.UtcNow;
        db.ApiKeys.Update(entity);
        await db.SaveChangesAsync(ct);

        return ApiKeyValidationResult.Valid(entity.UserId, entity.Scopes);
    }

    public async Task RevokeAsync(Guid keyId, CancellationToken ct)
    {
        var entity = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (entity is not null)
        {
            entity.IsRevoked = true;
            entity.LastUsedAt = DateTimeOffset.UtcNow;
            db.ApiKeys.Update(entity);
            await db.SaveChangesAsync(ct);
            log.LogInformation("API key revoked: {KeyName}", entity.KeyName);
        }
    }
}

// Endpoint: POST /v1/security/api-keys
// Endpoint: DELETE /v1/security/api-keys/{keyId}
// Endpoint: GET /v1/security/api-keys (list with last-used, expiry)
```

**Implementation:**

- [ ] Create API key management UI/API
- [ ] Enforce 90-day expiry on new keys
- [ ] Alert on key not used for 30 days
- [ ] Audit all key operations

---

## High-Priority Findings

### 🟠 HIGH-1: Database Encryption at Rest Not Enforced

**Current State:**

```json
{
  "ConnectionStrings": {
    "Postgres": "Server=postgres.example.com;Database=hope_agent;..."
    // ← No mention of SSL, TDE, encryption
  }
}
```

**Risk:**

- Unencrypted PostgreSQL on disk (vm.pgsql.com) = patient data readable if drive stolen
- Backup files in S3 unencrypted

**Fix:**

```csharp
// Program.cs
services.AddDbContextPool<AgentDbContext>(o =>
{
    var connStr = cfg.GetConnectionString("Postgres") ?? throw new InvalidOperationException();

    // Force SSL for Postgres connection
    if (!connStr.Contains("SSL Mode"))
        connStr += ";SSL Mode=Require;Trust Server Certificate=false";

    o.UseNpgsql(connStr, npg =>
    {
        npg.EnableRetryOnFailure(3);
        npg.CommandTimeout(30);
    });
}, poolSize: 128);

// Database-level: Enable PostgreSQL TDE
// SQL: ALTER SYSTEM SET ssl = on;
// SQL: ALTER SYSTEM SET ssl_cert_file = '/etc/postgresql/server.crt';
// SQL: SELECT pg_reload_conf();
```

**Implementation:**

- [ ] Enable PostgreSQL SSL enforcement
- [ ] Enable TDE (Transparent Data Encryption) if using cloud DB
- [ ] Encrypt backups with customer-managed keys (CMK)

---

### 🟠 HIGH-2: Redis & Qdrant Unencrypted (In-Transit & At-Rest)

**Current State:**

```csharp
// Infrastructure/DependencyInjection.cs
services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect("redis.example.com:6379"));  // ← No SSL!

// Qdrant
services.AddSingleton(_ => new QdrantClient("qdrant.example.com", 6334));  // ← No HTTPS
```

**Risk:**

- Redis stores cache + embedding vectors (PHI) in plaintext
- Network sniffing = patient data exposure

**Fix:**

```csharp
// Redis with SSL
services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var options = ConfigurationOptions.Parse("redis.example.com:6380,ssl=true");
    options.CertificateSelection += (_, __, ___, ____) =>
    {
        var cert = new X509Certificate2("/etc/ssl/certs/redis-client.pfx", "password");
        return cert;
    };
    return ConnectionMultiplexer.Connect(options);
});

// Qdrant with HTTPS
var qdrantOpts = cfg.GetSection("Qdrant").Get<QdrantOptions>() ?? new QdrantOptions();
if (!qdrantOpts.Host.StartsWith("https://"))
    qdrantOpts.Host = $"https://{qdrantOpts.Host}";

services.AddSingleton(_ => new QdrantClient(qdrantOpts.Host, qdrantOpts.Port, apiKey: qdrantOpts.ApiKey));
```

**Implementation:**

- [ ] Redis: Enable TLS 1.2+ with client cert auth
- [ ] Qdrant: Enable HTTPS + API key auth
- [ ] Monitor unencrypted connections (deny)

---

### 🟠 HIGH-3: Input Validation Incomplete (OWASP A03)

**Current State:**

- `SandboxedToolExecutor` validates JSON object type ✅
- But **no length limits** on tool arguments
- **No regex validation** on user message

**Risk:**

```
POST /v1/agent/chat
{
  "Message": "x" * 10_000_000,  // 10MB string → OOM crash
  "ConversationId": "'; DROP TABLE conversations; --"
}
```

**Fix:**

```csharp
// Application/Abstractions/AgentRequest.cs (add validation)
public sealed record AgentRequest(
    Guid UserId,
    Guid? ConversationId,
    [StringLength(8000)]  // Max 8KB message
    [RegularExpression(@"^[\p{L}\p{N}\p{P}\s]*$")]  // Printable + spaces
    string Message,
    string? AgentProfile = null,
    string? CorrelationId = null
);

// Add Data Annotations validation in endpoint
grp.MapPost("/chat", async (
    [FromBody] AgentChatRequest req,
    [FromServices] IAgentRuntime runtime,
    ...
) =>
{
    // Manual validation (since minimal API doesn't auto-validate by default)
    if (string.IsNullOrWhiteSpace(req.Message) || req.Message.Length > 8000)
        return Results.BadRequest("Message must be 1–8000 chars");

    if (req.Message.Contains("DROP TABLE") || req.Message.Contains("DELETE FROM"))
        return Results.BadRequest("SQL keywords not allowed");

    // ... rest
});

// Infrastructure/Application/SandboxedToolExecutor.cs (add limits)
public sealed class SandboxedToolExecutor : IToolExecutor
{
    private const int MaxArgumentsBytes = 64 * 1024;  // 64KB max args

    public async Task<string> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        var jsonBytes = Encoding.UTF8.GetByteCount(call.ArgumentsJson);
        if (jsonBytes > MaxArgumentsBytes)
            throw new ArgumentException($"Tool arguments exceed {MaxArgumentsBytes} bytes");

        // ... parse JSON
    }
}
```

**Implementation:**

- [ ] Add `StringLength`, `RegularExpression` attributes
- [ ] Validate in endpoint + data layer
- [ ] Log validation failures for monitoring
- [ ] Reject suspicious patterns (SQL keywords, script tags)

---

### 🟠 HIGH-4: Rate Limiter Bypass Vectors

**Current State:**

```csharp
// Program.cs, line 60–70
var key = ctx.User.Identity?.IsAuthenticated == true
    ? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)?.Value ?? "anon"
    : ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
```

**Risk:**

- If `UserId` claim is spoofed → rate limit bypass
- If `RemoteIpAddress` is null → falls back to "anon" (shared limit) → DoS friendly
- X-Forwarded-For header not validated → spoofing via proxy

**Fix:**

```csharp
// Security/RateLimitingMiddleware.cs (NEW)
public sealed class RateLimitingMiddleware(RequestDelegate next, ILogger<RateLimitingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        // Validate real IP (not spoofed X-Forwarded-For)
        var ip = ctx.Connection.RemoteIpAddress;

        // If behind reverse proxy, validate X-Forwarded-For is from trusted proxy only
        if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded))
        {
            var trustedProxies = new[] { "10.0.0.0/8", "172.16.0.0/12" };
            if (!IsInTrustedRange(ip?.ToString(), trustedProxies))
            {
                log.LogWarning("Suspicious X-Forwarded-For from untrusted IP: {Ip}", ip);
                ctx.Response.StatusCode = 403;
                return;
            }
            ip = IPAddress.Parse(forwarded.ToString().Split(',')[0].Trim());
        }

        ctx.Items["ClientIp"] = ip?.ToString() ?? "unknown";
        await next(ctx);
    }
}

// Update rate limit key resolution
var key = ctx.User.Identity?.IsAuthenticated == true
    ? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)?.Value
    : ctx.Items["ClientIp"]?.ToString();

if (string.IsNullOrEmpty(key))
{
    // Fail closed: reject if can't identify user/IP
    ctx.Response.StatusCode = 429;
    return;
}
```

**Implementation:**

- [ ] Validate rate limit key derivation
- [ ] Reject requests with missing IP
- [ ] Validate X-Forwarded-For only from trusted proxies
- [ ] Monitor rate limit distributions for anomalies

---

### 🟠 HIGH-5: Secrets Not Rotated / Stored Securely

**Current State:**

- LLM API keys (OpenAI, Anthropic, Gemini) in `appsettings.json` ← version control risk
- Kafka broker passwords plaintext
- Database password in connection string

**Risk:**

- Secrets in Git history → permanent compromise
- Exposed API keys = unlimited LLM calls at cost of company
- Database credentials leaked → patient data theft

**Fix:** Azure Key Vault + Managed Identity

```csharp
// Program.cs
if (app.Environment.IsProduction())
{
    var kvUri = $"https://{cfg["KeyVault:VaultName"]}.vault.azure.net/";
    var credential = new DefaultAzureCredential();

    builder.Configuration.AddAzureKeyVault(
        new Uri(kvUri),
        credential,
        new KeyVaultSecretManager());
}

// Access secrets
var openAiKey = cfg["LLM:OpenAI:ApiKey"];  // Fetched from KV, not config file
var pgPassword = cfg["ConnectionStrings:PgPassword"];  // From KV

// Rotation: change secret in KV → redeploy app (re-reads on startup)
```

**Secrets to manage:**

- [ ] `Jwt:Secret` → rotate every 90 days
- [ ] `LLM:OpenAI:ApiKey` → rotate every 6 months
- [ ] `LLM:Anthropic:ApiKey`
- [ ] `LLM:Gemini:ApiKey`
- [ ] `ConnectionStrings:Postgres` password
- [ ] `Kafka:BrokerPassword`
- [ ] `Redis:Password`

**Implementation:**

- [ ] Migrate all secrets to Key Vault
- [ ] Remove from `appsettings.json`
- [ ] Set up Key Vault backup + restore tests
- [ ] Audit Key Vault access logs weekly

---

### 🟠 HIGH-6: Audit Logging Incomplete

**Current State:**

- Tool approvals logged ✅
- Conversations logged ✅
- **But missing:** API access logs, permission changes, security events

**Risk:**

- Insider stealing patient data without audit trail
- Forensics impossible after breach
- HIPAA violation: "Audit controls must capture access to patient data" (§164.312(b))

**Fix:**

```csharp
// Application/Audit/IAuditLogger.cs (extend existing)
public interface IAuditLogger
{
    Task LogAsync(AuditEvent evt, CancellationToken ct);
}

// Domain/Audit/AuditEvent.cs (extend)
public sealed record AuditEvent
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string EventType { get; init; } = null!;  // "api_access", "tool_executed", "patient_data_retrieved"
    public string Resource { get; init; } = null!;  // "/v1/agent/chat", "tool:patient_lookup"
    public string Action { get; init; } = null!;  // "READ", "WRITE", "DELETE"
    public bool Success { get; init; }
    public string? ErrorReason { get; init; }
    public string? RedactedContent { get; init; }
    public string IpAddress { get; init; } = null!;
    public string UserAgent { get; init; } = null!;
    public Dictionary<string, string>? Metadata { get; init; }  // Additional context
}

// Infrastructure/Persistence/AuditLogStore.cs
public sealed class AuditLogStore(AgentDbContext db, ILogger<AuditLogStore> log) : IAuditLogger
{
    public async Task LogAsync(AuditEvent evt, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLogEntity
        {
            Id = evt.Id,
            UserId = evt.UserId,
            Timestamp = evt.Timestamp,
            EventType = evt.EventType,
            Resource = evt.Resource,
            Action = evt.Action,
            Success = evt.Success,
            ErrorReason = evt.ErrorReason,
            RedactedContent = evt.RedactedContent,
            IpAddress = evt.IpAddress,
            UserAgent = evt.UserAgent,
        });

        await db.SaveChangesAsync(ct);

        // Also send to immutable log (Azure Log Analytics / Splunk)
        log.LogInformation("Audit: {EventType} {Resource} {Action} by {UserId} from {Ip}",
            evt.EventType, evt.Resource, evt.Action, evt.UserId, evt.IpAddress);
    }
}

// Middleware to auto-log all API access
public sealed class ApiAuditLoggingMiddleware(RequestDelegate next, IAuditLogger auditLogger, ILogger<ApiAuditLoggingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var startTime = DateTimeOffset.UtcNow;
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = ctx.Request.Headers["User-Agent"].ToString();

        await next(ctx);

        // Log after response
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;

        await auditLogger.LogAsync(new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            UserId = Guid.TryParse(userId, out var uid) ? uid : Guid.Empty,
            Timestamp = DateTimeOffset.UtcNow,
            EventType = "api_access",
            Resource = ctx.Request.Path,
            Action = ctx.Request.Method,
            Success = ctx.Response.StatusCode < 400,
            ErrorReason = ctx.Response.StatusCode >= 400 ? $"HTTP {ctx.Response.StatusCode}" : null,
            IpAddress = ip,
            UserAgent = userAgent,
            Metadata = new()
            {
                ["duration_ms"] = duration.ToString(),
                ["status_code"] = ctx.Response.StatusCode.ToString(),
            }
        }, CancellationToken.None);
    }
}

// Register middleware
app.UseMiddleware<ApiAuditLoggingMiddleware>();
```

**Migration (new table):**

```sql
CREATE TABLE audit_logs (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL,
    timestamp TIMESTAMPTZ NOT NULL,
    event_type VARCHAR(128) NOT NULL,
    resource VARCHAR(512) NOT NULL,
    action VARCHAR(32) NOT NULL,
    success BOOLEAN NOT NULL,
    error_reason TEXT,
    redacted_content TEXT,
    ip_address VARCHAR(45) NOT NULL,
    user_agent TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_audit_user_timestamp ON audit_logs(user_id, timestamp DESC);
CREATE INDEX idx_audit_event_type ON audit_logs(event_type);
CREATE INDEX idx_audit_resource ON audit_logs(resource);
```

**Implementation:**

- [ ] Create audit log table + indexes
- [ ] Log all API access (via middleware)
- [ ] Log all tool executions
- [ ] Log all permission changes
- [ ] Retention: 7 years (HIPAA)
- [ ] Read-only storage (immutable append)

---

### 🟠 HIGH-7: API Versioning Not Implemented

**Current State:**

```csharp
// AgentEndpoints.cs
grp.MapGroup("/v1/agent").RequireAuthorization().WithTags("Agent");
```

**Risk:**

- Breaking changes in future phases → clients crash
- No way to deprecate endpoints
- Version-aware features (e.g., new RBAC rules) hard to roll out

**Fix:**

```csharp
// Program.cs
builder.Services.AddApiVersioning(o =>
{
    o.DefaultApiVersion = new ApiVersion(1, 0);
    o.AssumeDefaultVersionWhenUnspecified = true;
    o.ReportApiVersions = true;
    o.ApiVersionReader = ApiVersionReader.Combine(
        new UrlSegmentApiVersionReader(),  // /v1/agent
        new HeaderApiVersionReader("X-API-Version")  // X-API-Version: 1.0
    );
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Hope.Agent API", Version = "1.0" });
    c.SwaggerDoc("v2", new OpenApiInfo { Title = "Hope.Agent API", Version = "2.0" });
});

// Endpoint with versioning
[ApiVersion("1.0")]
[ApiVersion("2.0", Deprecated = true)]
grp.MapPost("/chat", ChatHandler).WithName("ChatV1");

// New endpoint in v2
[ApiVersion("2.0")]
grp.MapPost("/chat", ChatHandlerV2).WithName("ChatV2");
```

**Implementation:**

- [ ] Tag all endpoints with `[ApiVersion]`
- [ ] Plan v2 before v1 goes to prod
- [ ] Deprecation warnings in headers
- [ ] 6-month deprecation period before removal

---

## Medium-Priority Findings

### 🟡 MEDIUM-1–8: (Listed Below)

| #   | Finding                                               | Fix Complexity | Priority |
| --- | ----------------------------------------------------- | -------------- | -------- |
| 1   | Debug endpoints exposed (`/swagger`, `/openapi`)      | Low            | Med      |
| 2   | Request ID not propagated for forensics               | Low            | Med      |
| 3   | No rate limit for `/v1/diagnostics` (info disclosure) | Low            | Med      |
| 4   | Missing `Expect-CT` header (cert pinning)             | Low            | Med      |
| 5   | Tool timeout not enforced (DoS)                       | Med            | Med      |
| 6   | PII in error messages                                 | Med            | Med      |
| 7   | No dependency scanning (SCA)                          | Med            | Med      |
| 8   | Temporal workflow secrets not rotated                 | High           | Med      |

---

## Recommended Implementation Timeline

### **Phase 19A — Critical (Weeks 1–2)**

- [ ] Implement `SecurityHeadersMiddleware` (CRITICAL-2)
- [ ] Force HTTPS + HSTS (CRITICAL-1)
- [ ] Move secrets to Key Vault (CRITICAL-3, CRITICAL-6, CRITICAL-7)
- [ ] Enable structured audit logging (CRITICAL-4)
- [ ] Implement CORS (CRITICAL-5)

**Deliverable:** Security audit checkbox ✅ for production readiness

### **Phase 19B — High (Weeks 3–4)**

- [ ] JWT key rotation (CRITICAL-6)
- [ ] Database encryption + Redis TLS (HIGH-1, HIGH-2)
- [ ] Input validation + limits (HIGH-3)
- [ ] Rate limiter hardening (HIGH-4)
- [ ] Complete audit logging (HIGH-6)

**Deliverable:** HIPAA compliance validation

### **Phase 19C — Medium + Polish (Weeks 5–8)**

- [ ] API versioning
- [ ] Request ID propagation
- [ ] Debug endpoint lockdown
- [ ] Dependency scanning (SCA)
- [ ] Security testing (OAST, DAST)

**Deliverable:** Enterprise-grade security posture

---

## Monitoring & Metrics

Add Prometheus metrics:

```
hope_security_events_total{type="auth_failure|rbac_violation|injection_detected|rate_limit_exceeded"}
hope_api_key_rotation_days{key_name="jwt|openai|anthropic"}
hope_audit_log_lag_seconds
hope_failed_validations_total{type="json|length|pattern"}
hope_secrets_rotated_total{secret_type="jwt|api_key|db_password"}
```

Grafana dashboard:

- Auth failure rate (% per hour)
- RBAC violations detected
- Injection/XSS attempts blocked
- Rate limit hit rate
- Audit log ingestion lag

---

## Compliance Checklist

- [ ] HIPAA Security Rule (§164.312)
  - [ ] Access controls (§164.312(a)(2)(i))
  - [ ] Audit controls (§164.312(b))
  - [ ] Encryption (§164.312(a)(2)(i), §164.312(e)(2)(ii))
  - [ ] Key management (§164.312(a)(2)(i))
- [ ] OWASP ASVS Level 3 (API Security)
  - [ ] V1 — Architecture
  - [ ] V2 — Authentication
  - [ ] V3 — Session Management
  - [ ] V4 — Access Control
  - [ ] V13 — API & Web Service

- [ ] OWASP LLM Top 10 2025
  - [ ] LLM01 — Prompt Injection ✅ (NemoClaw rails)
  - [ ] LLM02 — Data Poisoning ✅ (retrieval rail)
  - [ ] LLM03 — Supply Chain ⚠️ (SCA needed)
  - [ ] LLM04 — Model Denial of Service ⚠️ (rate limiting)
  - [ ] LLM05 — Insufficient Sandboxing ✅ (execution limits)
  - [ ] LLM06 — Sensitive Info Disclosure ✅ (output shield + audit)
  - [ ] LLM07 — Insecure Plugin Design ✅ (JSON validation)
  - [ ] LLM08 — Excessive Agency ✅ (RBAC)
  - [ ] LLM09 — Overreliance ⚠️ (human review gates)
  - [ ] LLM10 — Unbounded Consumption ⚠️ (token limits)

---

## Risk Matrix

| Finding                           | Likelihood | Impact   | Risk Level   | Owner    | Deadline |
| --------------------------------- | ---------- | -------- | ------------ | -------- | -------- |
| HTTPS not enforced                | High       | Critical | **CRITICAL** | Security | Week 1   |
| SecurityHeadersMiddleware missing | High       | High     | **CRITICAL** | Eng      | Week 1   |
| JWT secret hardcoded              | Med        | Critical | **CRITICAL** | DevOps   | Week 1   |
| PHI logged plaintext              | High       | Critical | **CRITICAL** | Eng      | Week 1   |
| CORS missing                      | High       | High     | **CRITICAL** | Eng      | Week 1   |
| DB/Redis unencrypted              | Med        | High     | **HIGH**     | DevOps   | Week 2   |
| Input validation gaps             | High       | High     | **HIGH**     | Eng      | Week 2   |
| Rate limit bypass                 | Med        | High     | **HIGH**     | Eng      | Week 2   |
| Audit logging gaps                | Low        | High     | **HIGH**     | Eng      | Week 3   |

---

## Conclusion

**Risk Posture:** Currently **unfit for healthcare production**. Critical security gaps violate HIPAA and expose patient data.

**Path Forward:** Implement Phase 19 in 8 weeks → enterprise-grade security → pass audit → production release.

**Budget Estimate:**

- Security eng review + code: $80–120K
- Infra (Key Vault, TLS certs, logging): $20–30K
- Testing + audit + compliance: $40–60K
- **Total:** $140–210K

**ROI:** Eliminates $1M+ breach/penalty risk + enables healthcare market access.

---

**Next Steps:**

1. [ ] Security review approval (CISO/Compliance)
2. [ ] Create Phase 19 epic + sprint planning
3. [ ] Set up Key Vault + staging environment
4. [ ] Begin CRITICAL items (Week 1)

---

**Prepared by:** Senior Security Engineer  
**Date:** May 26, 2026  
**Classification:** INTERNAL — RESTRICTED
