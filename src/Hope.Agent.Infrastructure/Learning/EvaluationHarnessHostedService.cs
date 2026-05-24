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

    /// <summary>Drop in AvgJudgeScore that triggers a regression warning.</summary>
    private const double RegressionThreshold = 0.05;

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

                // Regression guard: compare with the previous completed run
                var recent = await harness.GetTrendAsync("default", days: 7, stoppingToken);
                if (recent.Count >= 2)
                {
                    var latest = recent[^1];
                    var previous = recent[^2];
                    var delta = latest.DeltaScore ?? 0;
                    if (delta <= -RegressionThreshold)
                    {
                        log.LogWarning(
                            "REGRESSION DETECTED: suite=default avg dropped {Delta:F3} " +
                            "(prev={Prev:F3} → now={Now:F3}). Review recent changes.",
                            delta, previous.AvgScore, latest.AvgScore);
                    }
                    else if (delta > 0)
                    {
                        log.LogInformation(
                            "Agent improved: suite=default avg +{Delta:F3} (now={Now:F3})",
                            delta, latest.AvgScore);
                    }
                }

                // Elo tournament: rank this run against the previous champion
                try
                {
                    var elo = await harness.RunEloTournamentAsync("default", stoppingToken);
                    log.LogInformation(
                        "Elo tournament: winner={Winner} elo={WinnerElo:F0} ({WinnerWins}W-{Draws}D)",
                        elo.WinnerId, elo.WinnerEloAfter, elo.WinnerWins, elo.Draws);
                }
                catch (InvalidOperationException)
                {
                    // Not enough runs yet — silently skip
                }
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

