using Hope.Agent.Application.Subagents;
using Hope.Agent.Application.Training;
using Hope.Agent.Application.Voice;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;

namespace Hope.Agent.Api.Endpoints;

public static class Phase12Endpoints
{
    public static IEndpointRouteBuilder MapTrainingEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/training").RequireAuthorization().WithTags("Training");

        grp.MapPost("/export", async (
            TrajectoryExportRequest req,
            ITrajectoryExporter exporter,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.ContentType = "application/x-ndjson";
            http.Response.Headers.ContentDisposition = "attachment; filename=trajectory.jsonl";
            var filter = new TrajectoryExportFilter(
                Since: req.Since,
                Until: req.Until,
                UserId: req.UserId,
                MaxConversations: req.MaxConversations,
                MinTurns: req.MinTurns ?? 2,
                RedactPhi: req.RedactPhi ?? true);
            var stats = await exporter.ExportAsync(filter, http.Response.Body, ct);
            http.Response.Headers["X-Export-Conversations"] = stats.Conversations.ToString();
            http.Response.Headers["X-Export-Messages"] = stats.Messages.ToString();
        });

        return app;
    }

    public static IEndpointRouteBuilder MapSubagentEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/subagents").RequireAuthorization().WithTags("Subagents");

        grp.MapPost("/fan-out", async (SubagentFanOutRequest req, ISubagentPool pool, CancellationToken ct) =>
        {
            if (req.Specs.Count == 0)
                return Results.BadRequest(new { error = "specs required" });
            var result = await pool.FanOutAsync(
                new SubagentRequest(req.UserId, req.Question,
                    req.Specs.Select(s => new SubagentSpec(s.Profile, s.SystemPromptHint ?? string.Empty)).ToList(),
                    req.CorrelationId),
                ct);
            return Results.Ok(result);
        });

        return app;
    }

    public static IEndpointRouteBuilder MapVoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/voice").RequireAuthorization().WithTags("Voice");

        grp.MapPost("/transcribe", async (HttpRequest req, ISpeechToText stt, CancellationToken ct) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data required" });
            var form = await req.ReadFormAsync(ct);
            var file = form.Files["file"];
            if (file is null) return Results.BadRequest(new { error = "file required" });
            string? lang = form["language"];
            await using var s = file.OpenReadStream();
            var result = await stt.TranscribeAsync(s, file.ContentType, lang, ct);
            return Results.Ok(result);
        }).DisableAntiforgery();

        grp.MapPost("/synthesize", async (SynthesizeRequest req, ITextToSpeech tts, CancellationToken ct) =>
        {
            var bytes = await tts.SynthesizeAsync(req.Text, req.Voice, ct);
            return Results.File(bytes.ToArray(), "audio/mpeg", "speech.mp3");
        });

        return app;
    }
}

public sealed record TrajectoryExportRequest(
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    Guid? UserId,
    int? MaxConversations,
    int? MinTurns,
    bool? RedactPhi);

public sealed record SubagentSpecDto(string Profile, string? SystemPromptHint);
public sealed record SubagentFanOutRequest(Guid UserId, string Question, IReadOnlyList<SubagentSpecDto> Specs, string? CorrelationId);

public sealed record SynthesizeRequest(string Text, string? Voice);
