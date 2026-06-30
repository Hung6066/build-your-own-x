using Hope.Agent.Application.Channels;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Channels;

internal sealed class DlpExternalChannel(
    IExternalChannel inner,
    IPhiRedactor phi,
    IOutputShield outputShield,
    IOptionsMonitor<DlpOptions> options,
    ILogger<DlpExternalChannel> log) : IExternalChannel
{
    public string Name => inner.Name;

    public async Task SendAsync(string recipientId, string text, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var safe = text ?? string.Empty;
        if (opts.Enabled && opts.ExternalChannels.Contains(inner.Name, StringComparer.OrdinalIgnoreCase))
        {
            var shielded = outputShield.Inspect(safe);
            if (shielded.HasLeak)
            {
                log.LogWarning("DLP redacted credential leak before {Channel} send: {Detections}", inner.Name, string.Join(",", shielded.Detections));
                HopeMeters.PhiRedactionCount.Add(1, new("channel", inner.Name), new("type", "secret"));
                safe = shielded.SafeContent;
            }

            if (opts.RedactPhiOnExternalChannels)
            {
                var redacted = phi.Redact(safe);
                if (!string.Equals(redacted, safe, StringComparison.Ordinal))
                {
                    log.LogWarning("DLP redacted PHI before {Channel} send to {Recipient}", inner.Name, recipientId);
                    HopeMeters.PhiRedactionCount.Add(1, new("channel", inner.Name), new("type", "phi"));
                }
                safe = redacted;
            }
        }

        await inner.SendAsync(recipientId, safe, ct).ConfigureAwait(false);
    }
}
