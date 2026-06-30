using System.Security.Claims;
using Hope.Agent.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

public static class ApiKeyLifecycleEndpoints
{
    public static IEndpointRouteBuilder MapApiKeyLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/security/api-keys")
            .RequireAuthorization()
            .WithTags("API Key Lifecycle");

        grp.MapGet("/", async (
            ClaimsPrincipal user,
            [FromServices] IApiKeyLifecycleStore store,
            [FromQuery] int take,
            CancellationToken ct) =>
        {
            var tenantId = TenantFromClaims(user);
            var rows = await store.ListAsync(tenantId, take == 0 ? 100 : take, ct);
            return Results.Ok(rows.Select(x => new
            {
                x.Id,
                x.TenantId,
                x.Name,
                x.Scope,
                x.Revoked,
                x.ExpiresAt,
                x.CreatedAt,
                x.RotatedAt,
                x.RevokedAt,
                x.CreatedBy,
                x.RevokedBy,
                x.Reason,
            }));
        });

        grp.MapPost("/", async (
            ClaimsPrincipal user,
            [FromBody] CreateApiKeyRequest req,
            [FromServices] IApiKeyLifecycleStore store,
            CancellationToken ct) =>
        {
            var tenantId = TenantFromClaims(user);
            var result = await store.CreateAsync(tenantId, req.Name, req.Scope ?? "hope-agent:mcp", req.ExpiresAt, Actor(user), ct);
            return Results.Ok(new
            {
                result.Id,
                result.Name,
                result.ExpiresAt,
                result.RawKey,
                note = "rawKey is returned once; store it in your secret manager",
            });
        });

        grp.MapPost("/{id:guid}/revoke", async (
            Guid id,
            ClaimsPrincipal user,
            [FromBody] RevokeApiKeyRequest? req,
            [FromServices] IApiKeyLifecycleStore store,
            CancellationToken ct) =>
        {
            var ok = await store.RevokeAsync(id, req?.Reason ?? "revoked", Actor(user), ct);
            return ok ? Results.Ok(new { revoked = true }) : Results.NotFound();
        });

        grp.MapPost("/{id:guid}/rotate", async (
            Guid id,
            ClaimsPrincipal user,
            [FromBody] RotateApiKeyRequest? req,
            [FromServices] IApiKeyLifecycleStore store,
            CancellationToken ct) =>
        {
            var result = await store.RotateAsync(id, req?.ExpiresAt, Actor(user), ct);
            return result is null
                ? Results.NotFound()
                : Results.Ok(new { result.Id, result.Name, result.ExpiresAt, result.RawKey, note = "rawKey is returned once" });
        });

        return app;
    }

    private static string Actor(ClaimsPrincipal user)
        => user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue("sub") ?? "unknown";

    private static Guid TenantFromClaims(ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue("tenant") ?? user.FindFirstValue("tenant_id");
        return Guid.TryParse(raw, out var tenantId) ? tenantId : SecurityDefaults.DefaultTenantId;
    }
}

public sealed record CreateApiKeyRequest(string Name, string? Scope, DateTimeOffset? ExpiresAt);
public sealed record RevokeApiKeyRequest(string? Reason);
public sealed record RotateApiKeyRequest(DateTimeOffset? ExpiresAt);
