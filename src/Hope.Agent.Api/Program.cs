using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;
using Hope.Agent.AgentRuntime;
using Hope.Agent.Api.Endpoints;
using Hope.Agent.Api.Health;
using Hope.Agent.Api.Middleware;
using Hope.Agent.Api.Security;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Hope.Agent.Infrastructure;
using Hope.Agent.Infrastructure.Security;
using Hope.Agent.LLMGateway;
using Hope.Agent.MultiAgent;
using Hope.Agent.Rag;
using Hope.Agent.Realtime;
using Hope.Agent.Api.Mcp;
using Hope.Agent.Tools;
using Hope.Agent.Tools.Mcp;
using Hope.Agent.Workflows;
using ModelContextProtocol.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateSlimBuilder(args);

// ── Global request body ceiling ───────────────────────────────────────────
// Drops the default Kestrel limit (30 MB) to 4 MB for all endpoints.
// Specific groups that need tighter limits use WithBodySizeLimit() to narrow further;
// groups that legitimately need more (e.g. file upload) can DisableRequestSizeLimit().
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 4 * 1024 * 1024);

var keyVaultEnabled = builder.Configuration.GetValue<bool>("KeyVault:Enabled");
var keyVaultName = builder.Configuration["KeyVault:VaultName"];
if ((builder.Environment.IsProduction() || keyVaultEnabled) && !string.IsNullOrWhiteSpace(keyVaultName))
{
    builder.Configuration.AddAzureKeyVault(
        new Uri($"https://{keyVaultName}.vault.azure.net/"),
        new DefaultAzureCredential(),
        new KeyVaultSecretManager());
}

builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .Enrich.FromLogContext()
       .Destructure.With<PhiDestructuringPolicy>()
       .WriteTo.Console();

    // ── H-3: SIEM integration (Splunk/Sentinel) ──────────────────────────
    var siemEnabled = ctx.Configuration.GetValue<bool>("Siem:Enabled");
    var siemEndpoint = ctx.Configuration["Siem:Endpoint"];
    if (siemEnabled && !string.IsNullOrWhiteSpace(siemEndpoint))
    {
        var siemToken = ctx.Configuration["Siem:Token"] ?? string.Empty;
        var siemHttp = new HttpClient();
        if (!string.IsNullOrWhiteSpace(siemToken))
            siemHttp.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", siemToken);

        cfg.WriteTo.Sink(new SiemSerilogSink(new SiemSink(siemHttp, siemEndpoint)));
    }
});

if (builder.Environment.IsDevelopment())
{
    var keyDirectory = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..",
        "..",
        "artifacts",
        "dataprotection-keys"));
    Directory.CreateDirectory(keyDirectory);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
}

builder.Services.AddAgentInfrastructure(builder.Configuration);
builder.Services.AddLLMGateway(builder.Configuration);
builder.Services.AddAgentTools(builder.Configuration);
builder.Services.Configure<RouteOptions>(o =>
    o.SetParameterPolicy<RegexInlineRouteConstraint>("regex"));
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(HopeAgentMcpServer).Assembly);
builder.Services.AddRag(builder.Configuration);
builder.Services.AddMultiAgent();
builder.Services.AddRealtime();
builder.Services.AddWorkflows(builder.Configuration);
builder.Services.AddAgentRuntime(builder.Configuration);
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection(WebhookOptions.Section));

// ── Trusted-proxy forwarded-header validation ─────────────────────────────
// Must be configured before UseForwardedHeaders so RemoteIpAddress is the
// real client IP when deployed behind a reverse proxy / load-balancer.
// Only add networks listed in ReverseProxy:TrustedNetworks (CIDR notation).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 2;          // Trust at most 2 proxy hops
    o.KnownNetworks.Clear();     // Start with explicit deny-all
    o.KnownProxies.Clear();

    // Loopback is always trusted (dev / health checks).
    o.KnownNetworks.Add(new IPNetwork(IPAddress.Loopback, 8));
    o.KnownNetworks.Add(new IPNetwork(IPAddress.IPv6Loopback, 128));

    var trustedCidrs = builder.Configuration
        .GetSection("ReverseProxy:TrustedNetworks").Get<string[]>() ?? [];
    foreach (var cidr in trustedCidrs)
    {
        var slash = cidr.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0
            && IPAddress.TryParse(cidr[..slash], out var addr)
            && int.TryParse(cidr[(slash + 1)..], out var prefix))
        {
            o.KnownNetworks.Add(new IPNetwork(addr, prefix));
        }
    }
});

builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: Bearer {token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);
builder.Services.AddExceptionHandler<SafeExceptionHandler>();
builder.Services.AddProblemDetails(opts =>
{
    opts.CustomizeProblemDetails = ctx =>
    {
        // Always stamp every problem response with a correlation ID for traceability.
        ctx.ProblemDetails.Extensions["correlationId"] = ctx.HttpContext.TraceIdentifier;

        // In production: strip detail and instance so internal paths/messages never reach clients.
        if (!ctx.HttpContext.RequestServices
                .GetRequiredService<IWebHostEnvironment>().IsDevelopment())
        {
            // Scrub through PHI redactor in case an upstream handler set Detail to an error string.
            var redactor = ctx.HttpContext.RequestServices
                .GetRequiredService<Hope.Agent.Application.Security.IPhiRedactor>();
            if (!string.IsNullOrEmpty(ctx.ProblemDetails.Detail))
                ctx.ProblemDetails.Detail = redactor.Redact(ctx.ProblemDetails.Detail);
            // Remove Instance (request path) — may contain PHI in query-strings.
            ctx.ProblemDetails.Instance = null;
        }
    };
});
builder.Services.AddMemoryCache();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var key = ctx.User.Identity?.IsAuthenticated == true
            ? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon"
            : ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 20,
            AutoReplenishment = true,
        });
    });
    // Per-user concurrency limiter for agent endpoints — prevents thread/LLM resource exhaustion
    // under spike load. Each user may run at most 3 agent calls simultaneously; up to 5 more wait.
    o.AddPolicy("agent-concurrency", ctx =>
    {
        var key = ctx.User.Identity?.IsAuthenticated == true
            ? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anon"
            : ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = 3,
            QueueLimit = 5,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    });
    // MCP endpoint 전용 rate limit (stricter)
    o.AddPolicy("mcp", ctx =>
    {
        var key = ctx.User.Identity?.IsAuthenticated == true
            ? $"mcp:{ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "anon"}"
            : $"mcp:{ctx.Connection.RemoteIpAddress}"
        ;
        var mcpOpts = ctx.RequestServices.GetRequiredService<IOptions<McpOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = mcpOpts.RateLimitPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
    o.AddPolicy("diagnostics", ctx =>
    {
        var key = ctx.User.Identity?.IsAuthenticated == true
            ? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "diagnostics:anon"
            : $"diagnostics:{ctx.Connection.RemoteIpAddress}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
    o.AddPolicy("openapi-docs", ctx =>
    {
        var key = ctx.User.Identity?.IsAuthenticated == true
            ? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "openapi:anon"
            : $"openapi:{ctx.Connection.RemoteIpAddress}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
    // Auth login: 10 req/min per IP — brute-force protection on credential exchange.
    o.AddPolicy("auth-login", ctx =>
    {
        var key = $"auth-login:{ctx.Connection.RemoteIpAddress}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
    // Auth refresh/revoke: 60 req/min per IP — tighter than global but allows normal rotation.
    o.AddPolicy("auth-refresh", ctx =>
    {
        var key = $"auth-refresh:{ctx.Connection.RemoteIpAddress}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

var jwt = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    })
    .AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, _ => { });
// ── H-2: Add .AddOpenIdConnect("oidc", ...) when Microsoft.AspNetCore.Authentication.OpenIdConnect package is installed ──
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<Hope.Agent.Application.Security.IJwtKeyProvider>((o, keyProvider) =>
    {
        var keySet = keyProvider.GetSigningKeys();
        var isRsa = string.Equals(keySet.Algorithm, "RS256", StringComparison.OrdinalIgnoreCase);
        if (!builder.Environment.IsDevelopment() && !isRsa && string.IsNullOrWhiteSpace(keySet.CurrentSecret))
            throw new InvalidOperationException("Jwt:Secret must be configured in production (prefer Key Vault). ");

        var signingKeys = new List<SecurityKey>();
        var currentKid = string.IsNullOrWhiteSpace(keySet.KeyId) ? "current" : keySet.KeyId;
        var previousKid = string.IsNullOrWhiteSpace(keySet.PreviousKeyId) ? "previous" : keySet.PreviousKeyId;

        if (isRsa)
        {
            var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportFromPem(keySet.CurrentPublicKeyPem);
            signingKeys.Add(new RsaSecurityKey(rsa) { KeyId = currentKid });
            if (!string.IsNullOrWhiteSpace(keySet.PreviousPublicKeyPem))
            {
                var prev = System.Security.Cryptography.RSA.Create();
                prev.ImportFromPem(keySet.PreviousPublicKeyPem);
                signingKeys.Add(new RsaSecurityKey(prev) { KeyId = previousKid });
            }
        }
        else
        {
            signingKeys.Add(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keySet.CurrentSecret)) { KeyId = currentKid });
            if (!string.IsNullOrWhiteSpace(keySet.PreviousSecret))
                signingKeys.Add(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keySet.PreviousSecret)) { KeyId = previousKid });
        }

        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKeys = signingKeys,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization(o =>
{
    // McpPolicy: accept either JWT Bearer (scope claim) OR API Key header
    o.AddPolicy("McpPolicy", p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthHandler.SchemeName)
        .RequireAuthenticatedUser()
        .RequireClaim("scope", "hope-agent:mcp"));

    // PatientAccess: caller must be admin/system, accessing their own data,
    // or have the target id explicitly listed in the "patients" claim.
    // Closes the broad-BOLA gap (C2).
    o.AddPolicy("PatientAccess", p => p
        .RequireAuthenticatedUser()
        .AddRequirements(new PatientAccessRequirement()));

    // TenantAccess: caller's "tenant" claim must match the resource tenant.
    // Closes the cross-tenant data-leak gap (C5).
    o.AddPolicy("TenantAccess", p => p
        .RequireAuthenticatedUser()
        .AddRequirements(new TenantRequirement()));

    // OpenApiAccess: only tokens with the openapi scope (or admin/system role) may
    // retrieve the API specification in non-development environments (H8).
    o.AddPolicy("OpenApiAccess", p => p
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx =>
            ctx.User.HasClaim("scope", "hope-agent:docs") ||
            ctx.User.IsInRole("admin") ||
            ctx.User.IsInRole("system")));
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IAuthorizationHandler, PatientAccessHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, TenantHandler>();

// ── Token issuance services ──────────────────────────────────────────────
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.AddSingleton<ITokenService, JwtTokenService>();

builder.Services.AddCors(o =>
{
    var allowedDomains = builder.Configuration.GetSection("Cors:AllowedDomains").Get<string[]>()
        ?? ["http://localhost:3000", "http://localhost:5000"];
    o.AddPolicy("StrictCors", p => p
        .WithOrigins(allowedDomains)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials()
        .WithExposedHeaders("X-Request-Id", "Idempotent-Replayed", "Retry-After")
        .SetPreflightMaxAge(TimeSpan.FromHours(1)));
});

var otlp = builder.Configuration["Otel:Endpoint"] ?? "http://localhost:4317";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("hope.agent.api"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("Hope.Agent.Runtime")
        .AddSource("Hope.Agent.MultiAgent")
        .AddSource("Hope.Agent.Workflows")
        .AddSource("Hope.Agent.Rag")
        .AddSource("Hope.Agent.LLM")
        // Scrub PHI from span attributes (http.url, db.statement, exception.message, etc.)
        // before they leave this process. Must be added before the OTLP exporter.
        .AddProcessor(new PhiSpanProcessor())
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlp)))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(HopeMeters.MeterName)
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlp)));

builder.Logging.AddOpenTelemetry(o =>
{
    o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("hope.agent.api"));
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
    o.AddOtlpExporter(e => e.Endpoint = new Uri(otlp));
});

var app = builder.Build();

// ── M-05: capture fire-and-forget Task failures that would otherwise be silently lost ──
TaskScheduler.UnobservedTaskException += (_, args) =>
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Hope.Agent.UnobservedTask");
    args.Exception.Flatten().Handle(ex =>
    {
        logger.LogWarning(ex, "Unobserved task exception captured by top-level handler");
        return true; // all handled — prevents process crash
    });
    args.SetObserved();
};

// ── Startup secret validation ─────────────────────────────────────────────
// Fail fast if mandatory secrets are absent or are still dev placeholders.
// Skipped automatically in Development so localhost runs are unaffected.
StartupSecretValidator.Validate(
    app.Configuration,
    app.Environment,
    app.Logger);

// Resolve real client IP from X-Forwarded-For before any IP-dependent middleware.
app.UseForwardedHeaders();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseMiddleware<ContentTypeGuardMiddleware>();
app.UseMiddleware<RequestContextMiddleware>();
app.UseMiddleware<ApiVersionGuardMiddleware>();
if (app.Environment.IsProduction())
{
    app.UseHttpsRedirection();
}
app.UseStatusCodePages();
app.UseCors("StrictCors");
app.UseMiddleware<SecurityHeadersMiddleware>();
// ── H-1: FHIR R4 validation middleware ──────────────────────────────────
app.UseFhirValidation();
app.UseRateLimiter();
app.UseAuthentication();
// TenantContextMiddleware MUST run after UseAuthentication so the JWT "tenant"
// claim is available and acts as source-of-truth over the X-Tenant-Id header.
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();
app.UseMiddleware<AuditLoggingMiddleware>();

var exposeOpenApiInProd = builder.Configuration.GetValue<bool>("OpenApi:EnabledInProduction");
if (app.Environment.IsDevelopment() || exposeOpenApiInProd)
{
    var openApiEndpoint = app.MapOpenApi();
    app.UseSwagger();
    if (app.Environment.IsDevelopment())
    {
        app.UseSwaggerUI(o =>
        {
            o.RoutePrefix = "swagger";
            o.SwaggerEndpoint("/swagger/v1/swagger.json", "Hope.Agent API v1");
        });
    }

    if (!app.Environment.IsDevelopment())
    {
        openApiEndpoint.RequireAuthorization("OpenApiAccess");
        openApiEndpoint.RequireRateLimiting("openapi-docs");
    }
}
// ── RFC 9116 security.txt ────────────────────────────────────────────────────
// Public endpoint — no authentication, no rate-limiting, intentionally cacheable.
// Tells security researchers where to report vulnerabilities.
app.MapGet("/.well-known/security.txt", (IConfiguration cfg) =>
{
    var contact = cfg["SecurityTxt:Contact"] ?? "mailto:security@hope.hospital.com";
    var policy = cfg["SecurityTxt:Policy"] ?? "https://hope.hospital.com/security-policy";
    var acks = cfg["SecurityTxt:Acknowledgments"];
    var langs = cfg["SecurityTxt:PreferredLanguages"] ?? "en";

    // RFC 9116 §2.5.5: Expires MUST be present; value MUST NOT be more than 1 year in the future.
    var expires = DateTimeOffset.UtcNow.AddMonths(11).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"Contact: {contact}");
    sb.AppendLine($"Expires: {expires}");
    sb.AppendLine($"Policy: {policy}");
    if (!string.IsNullOrWhiteSpace(acks))
        sb.AppendLine($"Acknowledgments: {acks}");
    sb.AppendLine($"Preferred-Languages: {langs}");

    return Results.Text(
        sb.ToString(),
        contentType: "text/plain; charset=utf-8");
})
.AllowAnonymous()
.WithTags("Meta")
.WithName("SecurityTxt")
.AddEndpointFilter(async (ctx, next) =>
{
    // RFC 9116 §2.5.4: responses SHOULD be cached.
    // Override the global no-store header set by SecurityHeadersMiddleware.
    ctx.HttpContext.Response.Headers.CacheControl = "public, max-age=86400";
    return await next(ctx);
});

app.MapHealthChecks("/healthz");
// ── H-6: K8s startup probe (initContainer waits for this before sending traffic) ──
app.MapGet("/healthz/startup", () => Results.Ok(new { status = "started", timestamp = DateTimeOffset.UtcNow }))
    .AllowAnonymous()
    .WithTags("Health");
app.MapHealthChecks("/healthz/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false,
});
app.MapHealthChecks("/healthz/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = h => h.Tags.Contains("ready"),
});
app.MapAuthEndpoints();
app.MapJwks();
app.MapAgentEndpoints();
app.MapRagEndpoints();
app.MapMemoryEndpoints();
app.MapAutonomyEndpoints();
app.MapMultiAgentEndpoints();
app.MapWorkflowEndpoints();
app.MapWebhookEndpoints();
app.MapNotificationsHub();
app.MapLearningEndpoints();
app.MapKnowledgeEndpoints();
app.MapShadowEndpoints();
app.MapAdversarialEndpoints();
app.MapApprovalEndpoints();
app.MapChannelEndpoints();
app.MapInsightEndpoints();
app.MapTrainingEndpoints();
app.MapSubagentEndpoints();
app.MapVoiceEndpoints();
app.MapDashboardEndpoints();
app.MapKanbanEndpoints();
app.MapMigrationEndpoints();
app.MapDiagnosticsEndpoints();
app.MapToolsEndpoints();
app.MapResearchEndpoints();
app.MapHarnessEndpoints();
app.MapEnterpriseSecurityEndpoints();
app.MapApiKeyLifecycleEndpoints();
app.MapMcp("/mcp")
    .RequireAuthorization("McpPolicy")
    .RequireRateLimiting("mcp");  // 30 req/min (configurable via Mcp:RateLimitPerMinute)

// ── FHIR R4 passthrough endpoint (H-1) ──────────────────────────────────
// The FhirValidationMiddleware already validates the payload. This endpoint
// simply echoes the validated resource back as confirmation.
app.MapPost("/v1/fhir/{resourceType}", async (string resourceType, HttpRequest request) =>
{
    request.EnableBuffering();
    using var reader = new StreamReader(request.Body, leaveOpen: true);
    var body = await reader.ReadToEndAsync();
    request.Body.Position = 0;

    return Results.Ok(new
    {
        resourceType,
        status = "validated",
        receivedAt = DateTimeOffset.UtcNow
    });
})
.AllowAnonymous()
.WithTags("FHIR");

await app.RunAsync();
