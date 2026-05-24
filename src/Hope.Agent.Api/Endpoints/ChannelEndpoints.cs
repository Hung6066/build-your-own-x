using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Channels;
using Hope.Agent.Infrastructure.Channels.Slack;
using Hope.Agent.Infrastructure.Channels.Zalo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Api.Endpoints;

public static class ChannelEndpoints
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/channels").WithTags("Channels");

        MapZalo(grp);
        MapSlack(grp);

        return app;
    }

    // -------- Zalo OA webhook --------
    // https://developers.zalo.me/docs/official-account/webhook
    private static void MapZalo(RouteGroupBuilder grp)
    {
        grp.MapPost("/zalo/webhook", async (
            HttpContext http,
            [FromServices] IOptions<ZaloOptions> zaloOpts,
            [FromServices] IChannelMessageRouter router,
            [FromServices] IChannelRegistry channels,
            CancellationToken ct) =>
        {
            var o = zaloOpts.Value;
            if (!o.Enabled) return Results.NotFound();

            http.Request.EnableBuffering();
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            var body = ms.ToArray();
            http.Request.Body.Position = 0;

            var sig = http.Request.Headers["X-ZEvent-Signature"].ToString();
            if (!VerifyZaloSignature(body, o.AppSecret, sig))
                return Results.Unauthorized();

            ZaloEvent? evt;
            try { evt = JsonSerializer.Deserialize<ZaloEvent>(body, s_json); }
            catch { return Results.BadRequest(new { error = "invalid_json" }); }
            if (evt is null || evt.EventName != "user_send_text")
                return Results.Ok(new { ok = true, ignored = evt?.EventName });

            var sender = evt.Sender?.Id ?? string.Empty;
            var text = evt.Message?.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(text))
                return Results.Ok(new { ok = true });

            if (o.AllowedSenderIds.Length > 0 && !o.AllowedSenderIds.Contains(sender))
                return Results.Ok(new { ok = true, rejected = "unauthorized_sender" });

            var reply = await router.RouteAsync(new InboundChannelMessage(
                Channel: "zalo",
                ExternalUserId: sender,
                ExternalChatId: sender,
                Text: text,
                AgentProfile: o.AgentProfile,
                CorrelationId: $"zalo:{evt.Message?.MsgId ?? sender}"), ct);

            var outbound = channels.Find("zalo");
            if (outbound is not null) await outbound.SendAsync(sender, reply, ct);

            return Results.Ok(new { ok = true });
        });
    }

    // -------- Slack Events API --------
    // https://api.slack.com/apis/connections/events-api
    private static void MapSlack(RouteGroupBuilder grp)
    {
        grp.MapPost("/slack/events", async (
            HttpContext http,
            [FromServices] IOptions<SlackOptions> slackOpts,
            [FromServices] IChannelMessageRouter router,
            [FromServices] IChannelRegistry channels,
            CancellationToken ct) =>
        {
            var o = slackOpts.Value;
            if (!o.Enabled) return Results.NotFound();

            http.Request.EnableBuffering();
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            var bodyBytes = ms.ToArray();
            http.Request.Body.Position = 0;

            var ts = http.Request.Headers["X-Slack-Request-Timestamp"].ToString();
            var sig = http.Request.Headers["X-Slack-Signature"].ToString();
            if (!VerifySlackSignature(bodyBytes, ts, sig, o.SigningSecret, o.MaxRequestSkewSeconds))
                return Results.Unauthorized();

            using var doc = JsonDocument.Parse(bodyBytes);
            var root = doc.RootElement;

            // URL verification handshake
            if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "url_verification")
            {
                var challenge = root.TryGetProperty("challenge", out var ch) ? ch.GetString() ?? string.Empty : string.Empty;
                return Results.Text(challenge, "text/plain");
            }

            if (root.TryGetProperty("type", out var t2) && t2.GetString() == "event_callback"
                && root.TryGetProperty("event", out var evt))
            {
                var evtType = evt.TryGetProperty("type", out var et) ? et.GetString() : null;
                // Ignore bot/edited messages to prevent loops
                if (evtType != "message" || (evt.TryGetProperty("bot_id", out _)) || (evt.TryGetProperty("subtype", out var st) && !string.IsNullOrEmpty(st.GetString())))
                    return Results.Ok(new { ok = true });

                var user = evt.TryGetProperty("user", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                var channel = evt.TryGetProperty("channel", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                var text = evt.TryGetProperty("text", out var tx) ? tx.GetString() ?? string.Empty : string.Empty;
                var tsEv = evt.TryGetProperty("ts", out var tsv) ? tsv.GetString() ?? string.Empty : string.Empty;

                if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(channel))
                    return Results.Ok(new { ok = true });

                if (o.AllowedChannelIds.Length > 0 && !o.AllowedChannelIds.Contains(channel))
                    return Results.Ok(new { ok = true, rejected = "unauthorized_channel" });

                // Slack expects an ack within 3 seconds — run agent work out-of-band.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var reply = await router.RouteAsync(new InboundChannelMessage(
                            Channel: "slack",
                            ExternalUserId: user,
                            ExternalChatId: channel,
                            Text: text,
                            AgentProfile: o.AgentProfile,
                            CorrelationId: $"slack:{tsEv}"), CancellationToken.None);

                        var outbound = channels.Find("slack");
                        if (outbound is not null) await outbound.SendAsync(channel, reply, CancellationToken.None);
                    }
                    catch { /* logged inside router/channel */ }
                }, CancellationToken.None);

                return Results.Ok(new { ok = true });
            }

            return Results.Ok(new { ok = true });
        });
    }

    // -------- Signature verification --------

    private static bool VerifyZaloSignature(byte[] body, string secret, string headerSig)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(headerSig)) return false;
        // Zalo signs sha256("<body><appsecret>") and sends as "sha256=<hex>" or raw hex.
        var concat = new byte[body.Length + Encoding.UTF8.GetByteCount(secret)];
        body.CopyTo(concat, 0);
        Encoding.UTF8.GetBytes(secret, concat.AsSpan(body.Length));
        var expected = Convert.ToHexString(SHA256.HashData(concat)).ToLowerInvariant();
        var got = headerSig.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? headerSig[7..]
            : headerSig;
        return FixedTimeEqualsHex(expected, got.ToLowerInvariant());
    }

    private static bool VerifySlackSignature(byte[] body, string timestamp, string signature, string secret, int maxSkewSeconds)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp)) return false;
        if (!long.TryParse(timestamp, out var ts)) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - ts) > maxSkewSeconds) return false;

        // base = "v0:" + timestamp + ":" + body
        var prefix = Encoding.UTF8.GetBytes($"v0:{timestamp}:");
        var basis = new byte[prefix.Length + body.Length];
        prefix.CopyTo(basis, 0);
        body.CopyTo(basis, prefix.Length);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = "v0=" + Convert.ToHexString(hmac.ComputeHash(basis)).ToLowerInvariant();
        return FixedTimeEqualsHex(expected, signature.ToLowerInvariant());
    }

    private static bool FixedTimeEqualsHex(string a, string b)
    {
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    }

    // -------- DTOs --------

    private sealed record ZaloEvent(
        [property: System.Text.Json.Serialization.JsonPropertyName("event_name")] string? EventName,
        [property: System.Text.Json.Serialization.JsonPropertyName("sender")] ZaloParty? Sender,
        [property: System.Text.Json.Serialization.JsonPropertyName("recipient")] ZaloParty? Recipient,
        [property: System.Text.Json.Serialization.JsonPropertyName("message")] ZaloMessage? Message);

    private sealed record ZaloParty(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string? Id);

    private sealed record ZaloMessage(
        [property: System.Text.Json.Serialization.JsonPropertyName("msg_id")] string? MsgId,
        [property: System.Text.Json.Serialization.JsonPropertyName("text")] string? Text);
}
