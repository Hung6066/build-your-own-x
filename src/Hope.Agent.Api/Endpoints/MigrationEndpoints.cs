using Hope.Agent.Application.Migration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hope.Agent.Api.Endpoints;

public static class MigrationEndpoints
{
    public static IEndpointRouteBuilder MapMigrationEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/migrate").RequireAuthorization().WithTags("Migration").DisableAntiforgery();

        grp.MapPost("", async (HttpRequest req, IExternalImporter importer, CancellationToken ct) =>
        {
            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });
            var form = await req.ReadFormAsync(ct);
            var sourceRaw = form["source"].ToString();
            if (!Enum.TryParse<ExternalSource>(sourceRaw, true, out var source) || source == ExternalSource.Unknown)
                return Results.BadRequest(new { error = "source must be one of: DialogflowFaq, Rasa, GenericFaq" });
            var file = form.Files["file"];
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "file required" });
            var dryRun = form["dryRun"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
            var intent = form["intent"].ToString() is { Length: > 0 } i ? i : null;
            await using var stream = file.OpenReadStream();
            var stats = await importer.ImportAsync(new ImportRequest(source, stream, intent, dryRun), ct);
            return Results.Ok(stats);
        });

        return app;
    }
}
