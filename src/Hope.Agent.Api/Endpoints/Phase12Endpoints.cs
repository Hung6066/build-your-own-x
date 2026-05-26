using Hope.Agent.Application.Learning;
using Hope.Agent.Application.Subagents;
using Hope.Agent.Application.Training;
using Hope.Agent.Application.Voice;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Domain.Training;
using Hope.Agent.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;
using System.Security.Claims;

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

        // ── DPO export ──────────────────────────────────────────────────────────
        grp.MapPost("/export/dpo", async (
            [FromBody] DpoExportRequest req,
            [FromServices] IDpoExporter exporter,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.ContentType = "application/x-ndjson";
            http.Response.Headers.ContentDisposition = "attachment; filename=dpo.jsonl";
            var filter = new DpoExportFilter(
                Since: req.Since,
                Until: req.Until,
                Specialty: req.Specialty,
                MaxRecords: req.MaxRecords ?? 5_000,
                RedactPhi: req.RedactPhi ?? true);
            var stats = await exporter.ExportAsync(filter, http.Response.Body, ct);
            http.Response.Headers["X-Export-Records"] = stats.Records.ToString();
        }).WithSummary("Export DPO preference pairs as JSONL for LoRA fine-tuning.");

        // ── Preference collection ───────────────────────────────────────────────
        grp.MapPost("/preference", async (
            [FromBody] PreferenceRequest req,
            [FromServices] IPreferenceStore store,
            [FromServices] IClock clock,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Prompt))
                return Results.BadRequest(new { error = "prompt required" });
            if (string.IsNullOrWhiteSpace(req.ChosenResponse) || string.IsNullOrWhiteSpace(req.RejectedResponse))
                return Results.BadRequest(new { error = "chosen and rejected required" });

            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) is string s && Guid.TryParse(s, out var uid) ? uid : Guid.Empty;
            var record = new PreferenceRecord
            {
                Id = Guid.CreateVersion7(),
                ConversationId = req.ConversationId,
                MessageId = req.MessageId ?? Guid.Empty,
                Prompt = req.Prompt,
                ChosenResponse = req.ChosenResponse,
                RejectedResponse = req.RejectedResponse,
                ChosenProvider = req.ChosenProvider,
                RejectedProvider = req.RejectedProvider,
                Rationale = req.Rationale,
                Specialty = req.Specialty,
                RatedByUserId = userId,
                CreatedAt = clock.UtcNow,
            };
            await store.AddAsync(record, ct);
            return Results.Accepted($"/v1/training/preference/{record.Id}", new { record.Id });
        }).WithSummary("Submit an A/B preference pair from a clinician for DPO training data.");

        grp.MapGet("/preference", async (
            [FromQuery] DateTimeOffset? since,
            [FromQuery] DateTimeOffset? until,
            [FromQuery] string? specialty,
            [FromQuery] int? take,
            [FromServices] IPreferenceStore store,
            CancellationToken ct) =>
        {
            var records = await store.QueryAsync(since, until, specialty, take ?? 100, ct);
            return Results.Ok(records);
        }).WithSummary("List preference records.");

        grp.MapGet("/preference/count", async (
            [FromQuery] DateTimeOffset? since,
            [FromServices] IPreferenceStore store,
            CancellationToken ct) =>
        {
            var count = await store.CountAsync(since, ct);
            return Results.Ok(new { count });
        });

        // ── Fine-tune jobs ──────────────────────────────────────────────────────
        grp.MapPost("/jobs", async (
            [FromBody] SubmitJobRequest req,
            [FromServices] IFinetuneJobService jobs,
            [FromServices] Microsoft.Extensions.Options.IOptions<FineTuningOptions> ftOpts,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) is string s && Guid.TryParse(s, out var uid) ? uid : Guid.Empty;
            if (!Enum.TryParse<FinetuneJobType>(req.JobType ?? "Dpo", ignoreCase: true, out var jobType))
                return Results.BadRequest(new { error = $"Unknown job type '{req.JobType}'" });

            var request = new FinetuneJobRequest(
                JobType: jobType,
                BaseModel: req.BaseModel ?? ftOpts.Value.DefaultBaseModel,
                DataSince: req.DataSince ?? DateTimeOffset.UtcNow.AddDays(-90),
                DataUntil: req.DataUntil ?? DateTimeOffset.UtcNow,
                CreatedByUserId: userId,
                OutputModelTag: req.OutputModelTag,
                MaxRecords: req.MaxRecords);
            var job = await jobs.SubmitAsync(request, ct);
            return Results.Accepted($"/v1/training/jobs/{job.Id}", job);
        }).WithSummary("Submit a LoRA fine-tune job to the Python training service.");

        grp.MapGet("/jobs", async (
            [FromQuery] int? take,
            [FromServices] IFinetuneJobService jobs,
            CancellationToken ct) =>
        {
            var list = await jobs.ListAsync(take ?? 20, ct);
            return Results.Ok(list);
        }).WithSummary("List fine-tune jobs, newest first.");

        grp.MapGet("/jobs/{id:guid}", async (
            Guid id,
            [FromServices] IFinetuneJobService jobs,
            CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        grp.MapPost("/jobs/{id:guid}/refresh", async (
            Guid id,
            [FromServices] IFinetuneJobService jobs,
            CancellationToken ct) =>
        {
            try
            {
                var job = await jobs.RefreshStatusAsync(id, ct);
                return Results.Ok(job);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).WithSummary("Poll training API and update the local job status.");

        grp.MapDelete("/jobs/{id:guid}", async (
            Guid id,
            [FromServices] IFinetuneJobService jobs,
            CancellationToken ct) =>
        {
            try
            {
                await jobs.CancelAsync(id, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).WithSummary("Cancel a running fine-tune job.");

        // ── Champion callback from Python training service ───────────────────────
        grp.MapPost("/champion", async (
            [FromBody] ChampionAnnounceRequest req,
            [FromServices] IAdaptiveRouter router,
            [FromServices] IShadowComparator shadow,
            [FromServices] IClock clock,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Tag))
                return Results.BadRequest(new { error = "tag required" });

            var intent = string.IsNullOrWhiteSpace(req.Specialty) ? "clinical" : req.Specialty;

            // Seed bandit stats so the UCB1 router starts preferring the local Ollama model
            await router.RecordOutcomeAsync(intent, "ollama", req.Tag,
                reward: 1.0, latencyMs: 0, failed: false, ct);

            // Register as a shadow challenger to begin automatic A/B evaluation
            var cfg = new ChallengerConfig
            {
                Id = Guid.CreateVersion7(),
                Intent = intent,
                ChallengerProvider = "ollama",
                TrafficFraction = 0.1,
                MinSamples = 50,
                PromotionWinRate = 0.55,
                Active = true,
                CreatedAt = clock.UtcNow,
            };
            await shadow.UpsertChallengerAsync(cfg, ct);

            return Results.Ok(new { req.Tag, intent, req.Elo });
        }).WithSummary("Python training service callback: register a promoted local LoRA champion and start shadow A/B evaluation.");

        return app;
    }

    private sealed record ChampionAnnounceRequest(string Tag, string? Specialty, double Elo);

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

public sealed record DpoExportRequest(
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    string? Specialty,
    int? MaxRecords,
    bool? RedactPhi);

public sealed record PreferenceRequest(
    Guid ConversationId,
    Guid? MessageId,
    string Prompt,
    string ChosenResponse,
    string RejectedResponse,
    string? ChosenProvider,
    string? RejectedProvider,
    string? Rationale,
    string? Specialty);

public sealed record SubmitJobRequest(
    string? JobType,
    string? BaseModel,
    string? OutputModelTag,
    DateTimeOffset? DataSince,
    DateTimeOffset? DataUntil,
    int? MaxRecords);

public sealed record SubagentSpecDto(string Profile, string? SystemPromptHint);
public sealed record SubagentFanOutRequest(Guid UserId, string Question, IReadOnlyList<SubagentSpecDto> Specs, string? CorrelationId);

public sealed record SynthesizeRequest(string Text, string? Voice);
