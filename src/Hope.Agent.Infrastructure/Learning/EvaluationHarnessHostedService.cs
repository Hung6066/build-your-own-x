using Hope.Agent.Application.Learning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Learning;

/// <summary>Runs the golden-suite once per day; first run delayed by 5 minutes after startup.</summary>
internal sealed class EvaluationHarnessHostedService(
    IServiceProvider sp,
    ILogger<EvaluationHarnessHostedService> log) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = sp.CreateAsyncScope();
                var harness = scope.ServiceProvider.GetRequiredService<IEvaluationHarness>();
                var run = await harness.RunSuiteAsync("default", stoppingToken);
                log.LogInformation("Eval suite default finished: passed={Passed} failed={Failed} avg={Avg:F3}",
                    run.Passed, run.Failed, run.AvgJudgeScore);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                log.LogError(ex, "Eval harness iteration failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
