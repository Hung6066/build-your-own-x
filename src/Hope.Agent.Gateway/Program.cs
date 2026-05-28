using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// ── Trusted-proxy forwarded-header validation ─────────────────────────────
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 2;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
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

var jwt = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        var currentSecret = jwt["CurrentSecret"] ?? jwt["Secret"];
        if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(currentSecret))
            throw new InvalidOperationException("Jwt current secret must be configured in production.");

        var signingKeys = new List<SecurityKey>
        {
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(currentSecret ?? "dev-secret-please-change-32+chars-min"))
            {
                KeyId = string.IsNullOrWhiteSpace(jwt["KeyId"]) ? "current" : jwt["KeyId"],
            },
        };
        var previousSecret = jwt["PreviousSecret"];
        if (!string.IsNullOrWhiteSpace(previousSecret))
        {
            signingKeys.Add(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(previousSecret))
            {
                KeyId = "previous",
            });
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
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(opts =>
{
    opts.AddFixedWindowLimiter("default", o =>
    {
        o.PermitLimit = 60;
        o.Window = TimeSpan.FromSeconds(60);
        o.QueueLimit = 0;
    });
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("hope.agent.gateway"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
        .AddOtlpExporter(o => o.Endpoint = new Uri(builder.Configuration["Otel:Endpoint"] ?? "http://localhost:4317")));

var app = builder.Build();
app.UseSerilogRequestLogging();
// Resolve real client IP from X-Forwarded-For before auth/rate-limit middleware.
app.UseForwardedHeaders();
if (app.Environment.IsProduction())
{
    app.UseHttpsRedirection();
}
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapReverseProxy();
await app.RunAsync();
