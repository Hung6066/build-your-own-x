using System.Text.Json;
using Hope.Agent.Application.Eventing;
using Hope.Agent.Domain.Eventing;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Eventing;

internal sealed class EfOutboxStore(IDbContextFactory<AgentDbContext> dbFactory) : IOutboxStore
{
    public async Task<Guid> AddAsync(OutboxEventWrite write, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var id = Guid.CreateVersion7();
        await db.OutboxEvents.AddAsync(ToEntity(write, id), ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return id;
    }

    public async Task<IReadOnlyList<OutboxEvent>> DueAsync(DateTimeOffset now, int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.OutboxEvents.AsNoTracking()
            .Where(x => (x.Status == OutboxEventStatus.Pending || x.Status == OutboxEventStatus.Failed)
                && x.ScheduledFor <= now
                && x.AttemptCount < x.MaxAttempts)
            .OrderBy(x => x.ScheduledFor)
            .ThenBy(x => x.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task MarkPublishingAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.OutboxEvents
            .Where(x => x.Id == id && (x.Status == OutboxEventStatus.Pending || x.Status == OutboxEventStatus.Failed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, OutboxEventStatus.Publishing)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
    }

    public async Task MarkPublishedAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.OutboxEvents
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, OutboxEventStatus.Published)
                .SetProperty(x => x.PublishedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(Guid id, string error, DateTimeOffset nextAttemptAt, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.OutboxEvents
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, OutboxEventStatus.Failed)
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.ScheduledFor, nextAttemptAt)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
    }

    public async Task MarkDeadLetterAsync(Guid id, string error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.OutboxEvents
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, OutboxEventStatus.DeadLetter)
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
    }

    public static OutboxEvent ToEntity(OutboxEventWrite write, Guid? id = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new OutboxEvent
        {
            Id = id ?? Guid.CreateVersion7(),
            TenantId = write.TenantId,
            Topic = write.Topic,
            Key = write.Key,
            PayloadJson = string.IsNullOrWhiteSpace(write.PayloadJson) ? "{}" : write.PayloadJson,
            HeadersJson = JsonSerializer.Serialize(write.Headers ?? new Dictionary<string, string>()),
            Status = OutboxEventStatus.Pending,
            MaxAttempts = Math.Max(write.MaxAttempts, 1),
            ScheduledFor = write.ScheduledFor ?? now,
            CorrelationId = write.CorrelationId,
            IdempotencyKey = write.IdempotencyKey,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}

internal sealed class OutboxPublisherWorker(
    IServiceScopeFactory scopeFactory,
    Microsoft.Extensions.Options.IOptionsMonitor<OutboxOptions> options,
    Microsoft.Extensions.Logging.ILogger<OutboxPublisherWorker> log) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = options.CurrentValue;
            try
            {
                if (opts.Enabled)
                {
                    using var scope = scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
                    var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
                    var due = await store.DueAsync(DateTimeOffset.UtcNow, opts.BatchSize, stoppingToken).ConfigureAwait(false);
                    foreach (var evt in due)
                    {
                        await PublishOneAsync(store, publisher, evt, opts, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Outbox publisher pass failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(opts.PollSeconds, 1)), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PublishOneAsync(IOutboxStore store, IEventPublisher publisher, OutboxEvent evt, OutboxOptions opts, CancellationToken ct)
    {
        try
        {
            await store.MarkPublishingAsync(evt.Id, ct).ConfigureAwait(false);
            await publisher.PublishAsync(evt.Topic, evt.Key, evt.PayloadJson, ct).ConfigureAwait(false);
            await store.MarkPublishedAsync(evt.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var attempt = evt.AttemptCount + 1;
            if (attempt >= Math.Min(evt.MaxAttempts, Math.Max(opts.MaxAttempts, 1)))
            {
                await store.MarkDeadLetterAsync(evt.Id, ex.Message, ct).ConfigureAwait(false);
                return;
            }

            var jitter = Random.Shared.Next(0, Math.Max(opts.JitterSeconds, 1) + 1);
            var delaySeconds = Math.Min(opts.MaxBackoffSeconds, Math.Max(opts.BaseBackoffSeconds, 1) * Math.Pow(2, attempt - 1)) + jitter;
            await store.MarkFailedAsync(evt.Id, ex.Message, DateTimeOffset.UtcNow.AddSeconds(delaySeconds), ct).ConfigureAwait(false);
        }
    }
}
