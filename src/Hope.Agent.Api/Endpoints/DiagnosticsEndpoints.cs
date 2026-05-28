using Hope.Agent.Api.Middleware;
using Hope.Agent.Application.Context;
using Hope.Agent.Application.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hope.Agent.Api.Endpoints;

public static class DiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/diagnostics")
            .RequireAuthorization()
            .RequireRateLimiting("diagnostics")
            .WithBodySizeLimit(32 * 1024)
            .WithRequestValidation()
            .WithTags("Diagnostics");

        grp.MapGet("", async (IDiagnosticRunner runner, CancellationToken ct) =>
        {
            var report = await runner.RunAsync(ct);
            return Results.Ok(report);
        });

        grp.MapGet("/context", async (IClinicalContextProvider provider, string? profile, CancellationToken ct) =>
        {
            var frag = await provider.GetAsync(profile, ct);
            return frag is null ? Results.NotFound() : Results.Ok(frag);
        });

        grp.MapGet("/context/profiles", async (IClinicalContextProvider provider, CancellationToken ct) =>
        {
            var profiles = await provider.ListProfilesAsync(ct);
            return Results.Ok(profiles);
        });

        return app;
    }
}
