using System.Threading.Channels;
using Hope.Agent.Application.Rag;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Rag.Ingestion;

internal sealed class IngestionWorker(
    Channel<IngestRequest> channel,
    IServiceScopeFactory scopes,
    IOptions<RagOptions> opts,
    ILogger<IngestionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var workerCount = Math.Max(1, opts.Value.IngestionWorkers);
        var tasks = Enumerable.Range(0, workerCount).Select(i => RunWorkerAsync(i, ct)).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task RunWorkerAsync(int id, CancellationToken ct)
    {
        log.LogInformation("Ingestion worker #{Id} started", id);
        await foreach (var req in channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<IIngestionService>();
                await svc.IngestAsync(req, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Worker #{Id} failed to ingest {Title}", id, req.Title);
            }
        }
    }
}
