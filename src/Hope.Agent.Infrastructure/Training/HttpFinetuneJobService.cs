using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.Training;
using Hope.Agent.Domain.Training;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Training;

/// <summary>
/// Calls the Python FastAPI training service to submit/monitor LoRA fine-tune jobs.
/// Also persists <see cref="FinetuneJob"/> rows in the agent database.
/// </summary>
internal sealed class HttpFinetuneJobService(
    AgentDbContext db,
    IHttpClientFactory httpFactory,
    IOptions<FineTuningOptions> opts,
    IAdaptiveRouter router,
    ILogger<HttpFinetuneJobService> log) : IFinetuneJobService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<FinetuneJob> SubmitAsync(FinetuneJobRequest request, CancellationToken ct)
    {
        var job = new FinetuneJob
        {
            Id = Guid.CreateVersion7(),
            JobType = request.JobType,
            BaseModel = request.BaseModel,
            OutputModelTag = request.OutputModelTag ?? $"hope-clinical-{request.JobType.ToString().ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMddHHmm}",
            DataSince = request.DataSince,
            DataUntil = request.DataUntil,
            CreatedByUserId = request.CreatedByUserId,
            Status = FinetuneJobStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // Submit to Python training API
        using var client = BuildClient();
        try
        {
            var payload = new
            {
                job_id = job.Id.ToString(),
                job_type = job.JobType.ToString().ToLowerInvariant(),
                base_model = job.BaseModel,
                output_tag = job.OutputModelTag,
                data_since = job.DataSince,
                data_until = job.DataUntil,
                max_records = request.MaxRecords,
            };

            var resp = await client.PostAsJsonAsync("/jobs", payload, Json, ct);
            resp.EnsureSuccessStatusCode();

            var remoteResp = await resp.Content.ReadFromJsonAsync<RemoteJobResponse>(Json, ct);
            job.RemoteJobId = remoteResp?.JobId ?? job.Id.ToString();
            job.Status = FinetuneJobStatus.Preparing;
            job.StartedAt = DateTimeOffset.UtcNow;
            log.LogInformation("FinetuneJob {JobId} submitted as remote {RemoteId}.", job.Id, job.RemoteJobId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to submit FinetuneJob {JobId} to training API.", job.Id);
            job.Status = FinetuneJobStatus.Failed;
            job.ErrorDetail = ex.Message;
        }

        await db.FinetuneJobs.AddAsync(job, ct);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<FinetuneJob> RefreshStatusAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.FinetuneJobs.FindAsync([jobId], ct)
                  ?? throw new KeyNotFoundException($"FinetuneJob {jobId} not found.");

        if (job.Status is FinetuneJobStatus.Completed or FinetuneJobStatus.Failed or FinetuneJobStatus.Cancelled)
            return job;

        using var client = BuildClient();
        try
        {
            var resp = await client.GetFromJsonAsync<RemoteJobStatusResponse>(
                $"/jobs/{job.RemoteJobId}", Json, ct);

            if (resp is null) return job;

            job.ProgressJson = JsonSerializer.Serialize(resp.Progress, Json);

            var newStatus = resp.Status?.ToLowerInvariant() switch
            {
                "pending" => FinetuneJobStatus.Pending,
                "preparing" => FinetuneJobStatus.Preparing,
                "training" => FinetuneJobStatus.Training,
                "evaluating" => FinetuneJobStatus.Evaluating,
                "completed" => FinetuneJobStatus.Completed,
                "failed" => FinetuneJobStatus.Failed,
                "cancelled" => FinetuneJobStatus.Cancelled,
                _ => job.Status,
            };

            job.Status = newStatus;

            if (newStatus is FinetuneJobStatus.Completed)
            {
                job.FinishedAt = DateTimeOffset.UtcNow;
                job.OutputModelTag = resp.OutputModelTag ?? job.OutputModelTag;
                job.EloScore = resp.EloScore;
                log.LogInformation("FinetuneJob {JobId} completed. Model tag: {Tag}, Elo: {Elo}.",
                    jobId, job.OutputModelTag, job.EloScore);

                // Auto-register in routing if Elo meets threshold
                if (job.EloScore >= opts.Value.AutoRegisterEloThreshold && !string.IsNullOrWhiteSpace(job.OutputModelTag))
                {
                    await router.RecordOutcomeAsync("clinical", "ollama", job.OutputModelTag!, reward: 1.0, latencyMs: 0, failed: false, ct);
                    log.LogInformation("Auto-registered fine-tuned model '{Tag}' in bandit router for 'clinical'.", job.OutputModelTag);
                }
            }
            else if (newStatus is FinetuneJobStatus.Failed)
            {
                job.FinishedAt = DateTimeOffset.UtcNow;
                job.ErrorDetail = resp.ErrorDetail;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to refresh status for FinetuneJob {JobId}.", jobId);
        }

        return job;
    }

    public async Task<FinetuneJob?> GetAsync(Guid jobId, CancellationToken ct)
        => await db.FinetuneJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);

    public async Task<IReadOnlyList<FinetuneJob>> ListAsync(int take, CancellationToken ct)
        => await db.FinetuneJobs.AsNoTracking()
            .OrderByDescending(j => j.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

    public async Task CancelAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.FinetuneJobs.FindAsync([jobId], ct)
                  ?? throw new KeyNotFoundException($"FinetuneJob {jobId} not found.");

        if (!string.IsNullOrWhiteSpace(job.RemoteJobId))
        {
            using var client = BuildClient();
            try { await client.DeleteAsync($"/jobs/{job.RemoteJobId}", ct); }
            catch (Exception ex) { log.LogWarning(ex, "Remote cancel failed for job {Id}.", jobId); }
        }

        job.Status = FinetuneJobStatus.Cancelled;
        job.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private HttpClient BuildClient()
    {
        var client = httpFactory.CreateClient("finetune");
        client.BaseAddress = new Uri(opts.Value.TrainingApiUrl);
        if (!string.IsNullOrWhiteSpace(opts.Value.ApiKey))
            client.DefaultRequestHeaders.Add("X-Api-Key", opts.Value.ApiKey);
        return client;
    }

    // Remote API response contracts
    private sealed record RemoteJobResponse(string JobId);

    private sealed record RemoteJobStatusResponse(
        string? Status,
        object? Progress,
        string? OutputModelTag,
        double? EloScore,
        string? ErrorDetail);
}
