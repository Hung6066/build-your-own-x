using System.Net.Http.Json;
using Hope.Agent.Application.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Channels.Zalo;

/// <summary>
/// Outbound Zalo Official Account messaging via Customer Service API.
/// POST {ApiBaseUrl}/v3.0/oa/message/cs?access_token=…  body: {recipient:{user_id}, message:{text}}.
/// </summary>
internal sealed class ZaloChannel(
    IHttpClientFactory http,
    IOptionsMonitor<ZaloOptions> opts,
    ILogger<ZaloChannel> log) : IExternalChannel
{
    public const string ChannelName = "zalo";
    public string Name => ChannelName;

    public async Task SendAsync(string recipientId, string text, CancellationToken ct)
    {
        var o = opts.CurrentValue;
        if (!o.Enabled || string.IsNullOrWhiteSpace(o.OaAccessToken))
        {
            log.LogDebug("Zalo channel disabled or no access token; skipping send to {Recipient}", recipientId);
            return;
        }

        var truncated = text.Length > o.MaxReplyLength
            ? string.Concat(text.AsSpan(0, o.MaxReplyLength), "\n…")
            : text;

        var client = http.CreateClient("zalo");
        var url = $"{o.ApiBaseUrl.TrimEnd('/')}/v3.0/oa/message/cs?access_token={Uri.EscapeDataString(o.OaAccessToken)}";
        var payload = new
        {
            recipient = new { user_id = recipientId },
            message = new { text = truncated },
        };

        try
        {
            using var resp = await client.PostAsJsonAsync(url, payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                log.LogWarning("Zalo send to {Recipient} failed: {Status} {Body}", recipientId, resp.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Zalo send to {Recipient} threw", recipientId);
        }
    }
}
