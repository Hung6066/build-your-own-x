using System.Net.Http.Headers;
using System.Net.Http.Json;
using Hope.Agent.Application.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Channels.Slack;

/// <summary>
/// Outbound Slack messaging via Web API chat.postMessage.
/// Recipient is a Slack channel ID (Cxxxxx) or DM channel (Dxxxxx).
/// </summary>
internal sealed class SlackChannel(
    IHttpClientFactory http,
    IOptionsMonitor<SlackOptions> opts,
    ILogger<SlackChannel> log) : IExternalChannel
{
    public const string ChannelName = "slack";
    public string Name => ChannelName;

    public async Task SendAsync(string recipientId, string text, CancellationToken ct)
    {
        var o = opts.CurrentValue;
        if (!o.Enabled || string.IsNullOrWhiteSpace(o.BotToken))
        {
            log.LogDebug("Slack channel disabled or no bot token; skipping send to {Recipient}", recipientId);
            return;
        }

        var truncated = text.Length > o.MaxReplyLength
            ? string.Concat(text.AsSpan(0, o.MaxReplyLength), "\n…")
            : text;

        var client = http.CreateClient("slack");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.BotToken);

        var url = $"{o.ApiBaseUrl.TrimEnd('/')}/chat.postMessage";
        var payload = new { channel = recipientId, text = truncated };

        try
        {
            using var resp = await client.PostAsJsonAsync(url, payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                log.LogWarning("Slack send to {Recipient} failed: {Status} {Body}", recipientId, resp.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Slack send to {Recipient} threw", recipientId);
        }
    }
}
