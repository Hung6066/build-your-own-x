using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// Conservative prompt-injection detector with dynamic learning. Static patterns block
/// well-known overrides/exfiltration; active adversarial signatures (auto-promoted from
/// past blocks) extend the block list at runtime.
/// </summary>
internal sealed partial class HeuristicPromptShield(
    IServiceScopeFactory scopeFactory,
    ILogger<HeuristicPromptShield> log) : IPromptShield
{
    private static readonly string[] HardBlocks =
    {
        "ignore previous instructions",
        "ignore all previous instructions",
        "disregard the above",
        "you are now a different",
        "system prompt:",
        "</system>",
        "<|im_start|>system",
    };

    private static readonly ConcurrentDictionary<string, string> ActiveSamples = new();

    public PromptShieldResult Inspect(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new PromptShieldResult(true, input, []);

        var lower = input.ToLowerInvariant();
        var reasons = new List<string>();

        foreach (var pattern in HardBlocks)
        {
            if (lower.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                reasons.Add($"hard:{pattern}");
        }

        if (RoleSpoofRx().IsMatch(input)) reasons.Add("role-spoof");
        if (DataExfilRx().IsMatch(input)) reasons.Add("exfil");

        foreach (var (sig, sample) in ActiveSamples)
        {
            if (lower.Contains(sample, StringComparison.Ordinal))
                reasons.Add($"learned:{sig[..8]}");
        }

        var sanitized = MarkerRx().Replace(input, " ");

        if (reasons.Count > 0)
        {
            HopeMeters.PromptShieldBlocks.Add(1, new KeyValuePair<string, object?>("reason", reasons[0]));
            HopeMeters.PromptInjectionDetected.Add(1, new KeyValuePair<string, object?>("source", "input"), new KeyValuePair<string, object?>("reason", reasons[0]));
            var hardHit = reasons.Exists(r =>
                r.StartsWith("hard:", StringComparison.Ordinal) ||
                r.StartsWith("learned:", StringComparison.Ordinal));

            _ = ObserveAsync(input, reasons[0]);
            return new PromptShieldResult(!hardHit, sanitized, reasons);
        }

        return new PromptShieldResult(true, sanitized, []);
    }

    private async Task ObserveAsync(string sample, string reason)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IAdversarialPatternStore>();
            await store.ObserveAsync(sample, reason, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Adversarial observe failed");
        }
    }

    internal static void RefreshActive(IEnumerable<(string Signature, string Sample)> patterns)
    {
        ActiveSamples.Clear();
        foreach (var (sig, sample) in patterns)
        {
            if (!string.IsNullOrWhiteSpace(sample))
                ActiveSamples[sig] = sample.ToLowerInvariant();
        }
    }

    [GeneratedRegex(@"<\|im_(start|end)\|>|<\/?(system|assistant|user)>", RegexOptions.IgnoreCase)]
    private static partial Regex MarkerRx();

    [GeneratedRegex(@"\b(?:act|pretend|roleplay)\s+as\s+(?:an?\s+)?(?:admin|root|developer|jailbroken)", RegexOptions.IgnoreCase)]
    private static partial Regex RoleSpoofRx();

    [GeneratedRegex(@"\b(?:print|leak|reveal|exfiltrate|dump)\s+(?:your|the)\s+(?:system\s+prompt|instructions|api\s*key|secrets?)", RegexOptions.IgnoreCase)]
    private static partial Regex DataExfilRx();
}

internal sealed class AdversarialAutoPromoter(IServiceScopeFactory scopeFactory, ILogger<AdversarialAutoPromoter> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private const int PromotionHitsThreshold = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IAdversarialPatternStore>();
                var all = await store.AllAsync(500, stoppingToken);
                foreach (var p in all)
                {
                    if (!p.Active && p.Hits >= PromotionHitsThreshold)
                    {
                        await store.PromoteAsync(p.Id, stoppingToken);
                        HopeMeters.AdversarialPromotions.Add(1);
                        log.LogInformation("Adversarial pattern auto-promoted sig={Sig} hits={Hits}", p.Signature[..8], p.Hits);
                    }
                }
                var active = await store.ActivePatternsAsync(stoppingToken);
                HeuristicPromptShield.RefreshActive(active.Select(p => (p.Signature, NormalizeSample(p.Sample))));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Adversarial auto-promoter iteration failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static string NormalizeSample(string s)
    {
        var trimmed = s.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80];
    }
}
