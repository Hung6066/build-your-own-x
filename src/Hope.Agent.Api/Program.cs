using System.Text;
using System.Threading.RateLimiting;
using Hope.Agent.AgentRuntime;
using Hope.Agent.Api.Endpoints;
using Hope.Agent.Api.Health;
using Hope.Agent.Api.Middleware;
using Hope.Agent.Api.Security;
using Hope.Agent.Application.Observability;
using Hope.Agent.Infrastructure;
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
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddAgentInfrastructure(builder.Configuration);
builder.Services.AddLLMGateway(builder.Configuration);
builder.Services.AddAgentTools(builder.Configuration);
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(HopeAgentMcpServer).Assembly);
builder.Services.AddRag(builder.Configuration);
builder.Services.AddMultiAgent();
builder.Services.AddRealtime();
builder.Services.AddWorkflows(builder.Configuration);
builder.Services.AddAgentRuntime(builder.Configuration);
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection(WebhookOptions.Section));

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);
builder.Services.AddProblemDetails();

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
            QueueLimit = 0,
            AutoReplenishment = true,
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
});

var jwt = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.RequireHttpsMetadata = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Secret"] ?? "dev-secret-please-change-32+chars-min")),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    })
    .AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(o =>
{
    // McpPolicy: accept either JWT Bearer (scope claim) OR API Key header
    o.AddPolicy("McpPolicy", p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthHandler.SchemeName)
        .RequireAuthenticatedUser()
        .RequireClaim("scope", "hope-agent:mcp"));
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

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/healthz/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false,
});
app.MapHealthChecks("/healthz/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = h => h.Tags.Contains("ready"),
});
app.MapAgentEndpoints();
app.MapRagEndpoints();
app.MapMemoryEndpoints();
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
app.MapMcp("/mcp")
    .RequireAuthorization("McpPolicy")
    .RequireRateLimiting("mcp");  // 30 req/min (configurable via Mcp:RateLimitPerMinute)

await app.RunAsync();
